using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace ZYC.LiveShare.Guest;

public sealed class PortForwarderOptions
{
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>
    ///     Chooses the local port for a host port. Defaults to the same port number;
    ///     if that port is taken an ephemeral port is used instead.
    /// </summary>
    public Func<int, int> MapPort { get; init; } = port => port;
}

public sealed record ForwardedPort(SharedServer Server, IPEndPoint LocalEndPoint);

/// <summary>
///     Forwards every server the host shares to a local TCP port, following sharing changes.
/// </summary>
public sealed class PortForwarder : IAsyncDisposable
{
    private readonly CancellationTokenSource disposeCancellation = new();
    private readonly ConcurrentDictionary<int, Forwarding> forwardings = new();
    private readonly ILogger logger;
    private readonly PortForwarderOptions options;
    private readonly GuestSession session;

    public PortForwarder(GuestSession session, PortForwarderOptions options, ILogger<PortForwarder> logger)
    {
        this.session = session;
        this.options = options;
        this.logger = logger;
    }

    public IReadOnlyCollection<ForwardedPort> ForwardedPorts => forwardings.Values.Select(f => f.Port).ToArray();

    public async ValueTask DisposeAsync()
    {
        session.ServerSharingStarted -= OnSharingStarted;
        session.ServerSharingChanged -= OnSharingStarted;
        session.ServerSharingStopped -= OnSharingStopped;
        disposeCancellation.Cancel();
        foreach (var sourcePort in forwardings.Keys)
        {
            StopForwarding(sourcePort);
        }

        await Task.CompletedTask;
    }

    public event EventHandler<ForwardedPort>? PortForwarded;
    public event EventHandler<ForwardedPort>? PortForwardingStopped;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        session.ServerSharingStarted += OnSharingStarted;
        session.ServerSharingChanged += OnSharingStarted;
        session.ServerSharingStopped += OnSharingStopped;

        foreach (var server in await session.GetSharedServersAsync(cancellationToken))
        {
            StartForwarding(server);
        }
    }

    private void OnSharingStarted(object? sender, ServerSharingEventArgs e)
    {
        StartForwarding(e.Server);
    }

    private void OnSharingStopped(object? sender, ServerSharingEventArgs e)
    {
        StopForwarding(e.Server.SourcePort);
    }

    private void StartForwarding(SharedServer server)
    {
        if (disposeCancellation.IsCancellationRequested)
        {
            return;
        }

        if (forwardings.TryGetValue(server.SourcePort, out var existing))
        {
            existing.Server = server;
            return;
        }

        var listener = CreateListener(options.MapPort(server.SourcePort), server.SourcePort);
        var forwarding = new Forwarding(server, listener,
            CancellationTokenSource.CreateLinkedTokenSource(disposeCancellation.Token));
        if (!forwardings.TryAdd(server.SourcePort, forwarding))
        {
            listener.Stop();
            return;
        }

        _ = AcceptLoopAsync(forwarding);
        logger.LogInformation(
            "Forwarding {Local} -> host port {Port}{Name}",
            forwarding.Port.LocalEndPoint, server.SourcePort,
            string.IsNullOrEmpty(server.SessionName) ? "" : $" ({server.SessionName})");
        PortForwarded?.Invoke(this, forwarding.Port);
    }

    private void StopForwarding(int sourcePort)
    {
        if (forwardings.TryRemove(sourcePort, out var forwarding))
        {
            forwarding.Cancellation.Cancel();
            forwarding.Listener.Stop();
            logger.LogInformation("Stopped forwarding host port {Port}", sourcePort);
            PortForwardingStopped?.Invoke(this, forwarding.Port);
        }
    }

    private TcpListener CreateListener(int preferredPort, int sourcePort)
    {
        try
        {
            var listener = new TcpListener(options.BindAddress, preferredPort);
            listener.Start();
            return listener;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse && preferredPort != 0)
        {
            logger.LogWarning("Local port {Port} is in use; forwarding host port {SourcePort} to a free port instead",
                preferredPort, sourcePort);
            var listener = new TcpListener(options.BindAddress, 0);
            listener.Start();
            return listener;
        }
    }

    private async Task AcceptLoopAsync(Forwarding forwarding)
    {
        try
        {
            while (!forwarding.Cancellation.IsCancellationRequested)
            {
                var client = await forwarding.Listener.AcceptSocketAsync(forwarding.Cancellation.Token);
                _ = BridgeAsync(client, forwarding);
            }
        }
        catch (Exception) when (forwarding.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning("Listener for host port {Port} failed: {Message}", forwarding.Server.SourcePort,
                ex.Message);
            StopForwarding(forwarding.Server.SourcePort);
        }
    }

    private async Task BridgeAsync(Socket client, Forwarding forwarding)
    {
        client.NoDelay = true;
        var server = forwarding.Server;
        var clientEndPoint = client.RemoteEndPoint;
        await using var local = new NetworkStream(client, true);
        Stream remote;
        try
        {
            remote = await session.OpenSharedServerStreamAsync(server, forwarding.Cancellation.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not open a stream to host port {Port}: {Message}", server.SourcePort, ex.Message);
            return;
        }

        logger.LogDebug("Connection {Client} -> host port {Port} opened", clientEndPoint, server.SourcePort);
        await using (remote)
        {
            using var bridgeCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(forwarding.Cancellation.Token);
            var upstream = local.CopyToAsync(remote, bridgeCancellation.Token);
            var downstream = remote.CopyToAsync(local, bridgeCancellation.Token);
            try
            {
                await Task.WhenAny(upstream, downstream);
            }
            finally
            {
                bridgeCancellation.Cancel();
            }

            await Task.WhenAll(upstream, downstream).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        logger.LogDebug("Connection {Client} -> host port {Port} closed", clientEndPoint, server.SourcePort);
    }

    private sealed class Forwarding(SharedServer server, TcpListener listener, CancellationTokenSource cancellation)
    {
        public SharedServer Server { get; set; } = server;
        public TcpListener Listener { get; } = listener;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public ForwardedPort Port => new(Server, (IPEndPoint)Listener.LocalEndpoint);
    }
}