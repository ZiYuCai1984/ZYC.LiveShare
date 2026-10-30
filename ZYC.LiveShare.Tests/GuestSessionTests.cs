using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ZYC.LiveShare.Guest;
using ZYC.LiveShare.Rpc;
using ZYC.LiveShare.Service;
using ZYC.LiveShare.Transport;

namespace ZYC.LiveShare.Tests;

public class GuestSessionTests
{
    private const string WorkspaceId = "0123456789abcdef0123456789abcdef0123";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task JoinsAndForwardsSharedServer()
    {
        using var echo = new EchoServer();
        await using var host = new FakeLiveShareHost("session-token");
        host.SharedPorts[echo.Port] = "echo";

        await using var guest = await JoinAsync(host, host.SessionToken, host.HostPublicKey, "Tester");

        Assert.Equal(2, guest.SessionInfo.SessionNumber);
        var joinInfo = host.JoinInfo!.Value;
        Assert.Equal(WorkspaceId, joinInfo.GetProperty("id").GetString());
        Assert.Equal("local", joinInfo.GetProperty("connectionMode").GetString());
        Assert.Equal("Tester", joinInfo.GetProperty("anonymousUserName").GetString());

        await using var forwarder = new PortForwarder(
            guest, new PortForwarderOptions { MapPort = _ => 0 }, NullLogger<PortForwarder>.Instance);
        await forwarder.StartAsync();

        var forwarded = Assert.Single(forwarder.ForwardedPorts);
        Assert.Equal(echo.Port, forwarded.Server.SourcePort);
        await AssertEchoesAsync(forwarded.LocalEndPoint, "hello through live share");
        await AssertEchoesAsync(forwarded.LocalEndPoint, "second connection");
    }

    [Fact]
    public async Task FollowsSharingStartedAndStopped()
    {
        using var echo = new EchoServer();
        await using var host = new FakeLiveShareHost("session-token");
        await using var guest = await JoinAsync(host, host.SessionToken, host.HostPublicKey);
        await using var forwarder = new PortForwarder(
            guest, new PortForwarderOptions { MapPort = _ => 0 }, NullLogger<PortForwarder>.Instance);
        await forwarder.StartAsync();
        Assert.Empty(forwarder.ForwardedPorts);

        var started = new TaskCompletionSource<ForwardedPort>();
        var stopped = new TaskCompletionSource<ForwardedPort>();
        forwarder.PortForwarded += (_, p) => started.TrySetResult(p);
        forwarder.PortForwardingStopped += (_, p) => stopped.TrySetResult(p);

        host.SharedPorts[echo.Port] = "late";
        await host.NotifySharingAsync("sharingStarted", echo.Port);
        var forwarded = await started.Task.WaitAsync(Timeout);
        await AssertEchoesAsync(forwarded.LocalEndPoint, "late share");

        host.SharedPorts.TryRemove(echo.Port, out _);
        await host.NotifySharingAsync("sharingStopped", echo.Port);
        await stopped.Task.WaitAsync(Timeout);
        Assert.Empty(forwarder.ForwardedPorts);
    }

    [Fact]
    public async Task AnswersHostVersionRequestAndRejectsUnknownMethods()
    {
        await using var host = new FakeLiveShareHost("session-token");
        await using var guest = await JoinAsync(host, host.SessionToken, host.HostPublicKey);
        var hostRpc = await host.Rpc;

        var version = await hostRpc.InvokeAsync("version.exchangeVersions", [new { }], CancellationToken.None);
        Assert.Equal("1.0", version.GetProperty("version").GetString());

        var error = await Assert.ThrowsAsync<RpcRemoteException>(() =>
            hostRpc.InvokeAsync("guest.unknown", [], CancellationToken.None));
        Assert.Equal(RpcConnection.MethodNotFound, error.Code);
    }

    [Fact]
    public async Task LeavingUnjoinsTheWorkspace()
    {
        await using var host = new FakeLiveShareHost("session-token");
        var guest = await JoinAsync(host, host.SessionToken, host.HostPublicKey);

        await guest.DisposeAsync();

        Assert.True(host.GuestLeft);
        Assert.Equal("Left the session.", await guest.Ended.WaitAsync(Timeout));
    }

    [Fact]
    public async Task RejectsHostWithUnknownKey()
    {
        await using var host = new FakeLiveShareHost("session-token");
        await using var impostorKeySource = new FakeLiveShareHost("unused");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            JoinAsync(host, host.SessionToken, impostorKeySource.HostPublicKey));
    }

    [Fact]
    public async Task FailsWithWrongSessionToken()
    {
        await using var host = new FakeLiveShareHost("session-token");

        await Assert.ThrowsAnyAsync<Exception>(() => JoinAsync(host, "wrong-token", host.HostPublicKey));
    }

    private static async Task<GuestSession> JoinAsync(FakeLiveShareHost host, string sessionToken, string hostPublicKey,
        string? anonymousName = null)
    {
        var (guestStream, hostStream) = await RpcTests.CreateConnectedStreamsAsync();
        var hostConnecting = host.ConnectAsync(hostStream);
        var workspace = new SharedWorkspace
            { Id = WorkspaceId, SessionToken = sessionToken, HostPublicKeys = [hostPublicKey] };

        var joining = GuestSession.JoinHostAsync(
            WorkspaceId,
            workspace,
            new HostConnection(guestStream, ConnectionMode.Direct, "test"),
            anonymousName,
            Timeout,
            NullLoggerFactory.Instance,
            null,
            CancellationToken.None);
        try
        {
            return await joining.WaitAsync(Timeout);
        }
        finally
        {
            _ = hostConnecting.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private static async Task AssertEchoesAsync(IPEndPoint endPoint, string text)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(endPoint).WaitAsync(Timeout);
        var stream = client.GetStream();
        var payload = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(payload);

        var received = new byte[payload.Length];
        await stream.ReadExactlyAsync(received).AsTask().WaitAsync(Timeout);
        Assert.Equal(text, Encoding.UTF8.GetString(received));
    }
}