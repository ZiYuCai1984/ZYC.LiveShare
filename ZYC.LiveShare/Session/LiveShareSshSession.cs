using System.Diagnostics;
using System.Security.Claims;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;

namespace ZYC.LiveShare.Session;

/// <summary>
///     The SSH session a Live Share guest runs over the host connection. JSON-RPC runs on a
///     <c>session</c> channel opened with the <c>json-rpc</c> request; each forwarded stream
///     gets its own channel claimed with a <c>stream-transport-{id}</c> request.
/// </summary>
public sealed class LiveShareSshSession : IAsyncDisposable
{
    private const string RpcChannelRequest = "json-rpc";
    private const string StreamChannelRequestPrefix = "stream-transport-";

    private readonly SshClientSession session;

    private LiveShareSshSession(SshClientSession session, SshStream rpcStream)
    {
        this.session = session;
        RpcStream = rpcStream;
    }

    public Stream RpcStream { get; }

    public bool IsClosed => session.IsClosed;

    public async ValueTask DisposeAsync()
    {
        if (!session.IsClosed)
        {
            await session.CloseAsync(SshDisconnectReason.ByApplication, "Guest left the session.");
        }

        session.Dispose();
    }

    public event EventHandler<SshSessionClosedEventArgs>? Closed
    {
        add => session.Closed += value;
        remove => session.Closed -= value;
    }

    /// <param name="hostPublicKeys">Base64 host keys published by the service; the host must present one of them.</param>
    /// <param name="sessionToken">The guest's workspace session token, sent as the SSH password.</param>
    public static async Task<LiveShareSshSession> ConnectAsync(
        Stream transport,
        IReadOnlyCollection<string> hostPublicKeys,
        string sessionToken,
        TraceSource trace,
        CancellationToken cancellationToken)
    {
        var knownKeys = hostPublicKeys.Select(Convert.FromBase64String).ToArray();
        var session = new SshClientSession(new SshSessionConfiguration(), trace);
        session.Authenticating += (_, e) =>
        {
            if (e.AuthenticationType == SshAuthenticationType.ServerPublicKey)
            {
                e.AuthenticationTask =
                    Task.FromResult(IsKnownHostKey(e.PublicKey, knownKeys) ? new ClaimsPrincipal() : null);
            }
        };

        try
        {
            await session.ConnectAsync(transport, cancellationToken);
            if (!await session.AuthenticateAsync(new SshClientCredentials(string.Empty, sessionToken),
                    cancellationToken))
            {
                throw new UnauthorizedAccessException("SSH authentication with the host failed.");
            }

            var channel = await session.OpenChannelAsync(
                new ChannelOpenMessage { ChannelType = SshChannel.SessionChannelType },
                new ChannelRequestMessage { RequestType = RpcChannelRequest, WantReply = true },
                cancellationToken);
            return new LiveShareSshSession(session, new SshStream(channel));
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Opens the data channel for a stream previously obtained from <c>streamManager.getStream</c>.
    /// </summary>
    /// <returns>The stream, or null if the host rejected the stream id.</returns>
    public async Task<Stream?> OpenStreamAsync(string streamId, CancellationToken cancellationToken)
    {
        var channel = await session.OpenChannelAsync(SshChannel.SessionChannelType, cancellationToken);
        var accepted = await channel.RequestAsync(
            new ChannelRequestMessage { RequestType = StreamChannelRequestPrefix + streamId, WantReply = true },
            cancellationToken);
        if (!accepted)
        {
            await channel.CloseAsync(cancellationToken);
            channel.Dispose();
            return null;
        }

        return new SshStream(channel);
    }

    private static bool IsKnownHostKey(IKeyPair? presented, byte[][] knownKeys)
    {
        // Like the official client, an empty key list means the host cannot be authenticated.
        var presentedBytes = presented?.GetPublicKeyBytes().ToArray();
        return presentedBytes is not null && knownKeys.Any(k => k.AsSpan().SequenceEqual(presentedBytes));
    }
}