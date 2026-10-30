using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using ZYC.LiveShare.Rpc;

namespace ZYC.LiveShare.Tests;

/// <summary>
///     Emulates the host side of the Live Share guest protocol, as observed in the official agent.
/// </summary>
internal sealed class FakeLiveShareHost : IAsyncDisposable
{
    private readonly TaskCompletionSource<RpcConnection> rpcReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SshServerSession session;
    private readonly ConcurrentDictionary<string, int> streamPorts = new();
    private int nextStreamId;

    public FakeLiveShareHost(string sessionToken)
    {
        SessionToken = sessionToken;
        HostKey = SshAlgorithms.PublicKey.RsaWithSha512.GenerateKeyPair();
        session = new SshServerSession(new SshSessionConfiguration(), new TraceSource("FakeHost", SourceLevels.Off))
        {
            Credentials = new SshServerCredentials(HostKey)
        };
        session.Authenticating += (_, e) =>
        {
            if (e.AuthenticationType == SshAuthenticationType.ClientPassword)
            {
                e.AuthenticationTask = Task.FromResult(e.Password == SessionToken
                    ? new ClaimsPrincipal(new ClaimsIdentity("guest"))
                    : null);
            }
        };
        session.ChannelOpening += (_, e) => e.Channel.Request += OnChannelRequest;
    }

    public string SessionToken { get; }

    public IKeyPair HostKey { get; }

    public string HostPublicKey => Convert.ToBase64String(HostKey.GetPublicKeyBytes().ToArray());

    /// <summary>Host ports shared with guests (port → description).</summary>
    public ConcurrentDictionary<int, string> SharedPorts { get; } = new();

    public JsonElement? JoinInfo { get; private set; }

    public bool GuestLeft { get; private set; }

    public Task<RpcConnection> Rpc => rpcReady.Task;

    public async ValueTask DisposeAsync()
    {
        if (Rpc.IsCompletedSuccessfully)
        {
            await Rpc.Result.DisposeAsync();
        }

        session.Dispose();
    }

    public Task ConnectAsync(Stream stream)
    {
        return session.ConnectAsync(stream);
    }

    public async Task NotifySharingAsync(string eventName, int port)
    {
        var rpc = await Rpc;
        await rpc.NotifyAsync($"serverSharing.{eventName}", new { server = CreateSharedServer(port) },
            CancellationToken.None);
    }

    private void OnChannelRequest(object? sender, SshRequestEventArgs<ChannelRequestMessage> e)
    {
        var channel = (SshChannel)sender!;
        if (e.RequestType == "json-rpc")
        {
            e.IsAuthorized = true;
            var rpc = new RpcConnection(new SshStream(channel), NullLogger.Instance)
                { RequestHandler = HandleRequestAsync };
            rpc.Start();
            rpcReady.TrySetResult(rpc);
        }
        else if (e.RequestType?.StartsWith("stream-transport-") == true
                 && streamPorts.TryRemove(e.RequestType["stream-transport-".Length..], out var port))
        {
            e.IsAuthorized = true;
            _ = Task.Run(() => PumpToLocalPortAsync(new SshStream(channel), port));
        }
    }

    private Task<object?> HandleRequestAsync(RpcNotification request, CancellationToken cancellationToken)
    {
        object? result;
        switch (request.Method)
        {
            case "version.exchangeVersions":
                result = new { version = "1.0.0", protocolVersion = "2.2" };
                break;
            case "workspace.joinWorkspace":
                JoinInfo = request.Params[0].Clone();
                if (JoinInfo.Value.GetProperty("joiningUserSessionToken").GetString() != SessionToken)
                {
                    throw new RpcRemoteException(-32016, "Authentication failed.");
                }

                result = new { id = JoinInfo.Value.GetProperty("id").GetString(), name = "fake", sessionNumber = 2 };
                break;
            case "workspace.unjoinWorkspace":
                GuestLeft = true;
                result = null;
                break;
            case "serverSharing.getSharedServers":
                result = SharedPorts.Keys.Select(CreateSharedServer).ToArray();
                break;
            case "streamManager.getStream":
                var condition = request.Params[1].GetString();
                if (request.Params[0].GetString() == "server_sharing" && int.TryParse(condition, out var port) &&
                    SharedPorts.ContainsKey(port))
                {
                    var id = $"stream-{Interlocked.Increment(ref nextStreamId)}";
                    streamPorts[id] = port;
                    result = id;
                }
                else
                {
                    result = null;
                }

                break;
            default:
                throw new RpcRemoteException(RpcConnection.MethodNotFound, $"Method not found: {request.Method}");
        }

        return Task.FromResult(result);
    }

    private object CreateSharedServer(int port)
    {
        return new
        {
            sourcePort = port,
            sessionName = SharedPorts.GetValueOrDefault(port),
            streamName = "server_sharing",
            streamCondition = port.ToString(),
            privacy = "private"
        };
    }

    private static async Task PumpToLocalPortAsync(Stream channelStream, int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var local = client.GetStream();
        await using (channelStream)
        {
            await Task.WhenAny(channelStream.CopyToAsync(local), local.CopyToAsync(channelStream));
        }
    }
}

/// <summary>A TCP server on loopback that echoes everything back, standing in for the host's web server.</summary>
internal sealed class EchoServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);

    public EchoServer()
    {
        listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public void Dispose()
    {
        listener.Stop();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        await client.GetStream().CopyToAsync(client.GetStream());
                    }
                });
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }
}