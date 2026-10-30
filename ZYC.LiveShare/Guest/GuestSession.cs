using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ZYC.LiveShare.Auth;
using ZYC.LiveShare.Rpc;
using ZYC.LiveShare.Service;
using ZYC.LiveShare.Session;
using ZYC.LiveShare.Transport;

namespace ZYC.LiveShare.Guest;

/// <summary>
///     A guest connection to a Live Share session:
///     token → <c>PUT workspace/{id}/user</c> → relay/direct → SSH → JSON-RPC → <c>workspace.joinWorkspace</c>.
/// </summary>
public sealed class GuestSession : IAsyncDisposable
{
    private readonly TaskCompletionSource<string> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger logger;
    private readonly LiveShareSshSession ssh;
    private int disposed;

    private GuestSession(
        SharedWorkspace workspace,
        HostConnection connection,
        LiveShareSshSession ssh,
        RpcConnection rpc,
        ILogger logger)
    {
        Workspace = workspace;
        Connection = connection;
        this.ssh = ssh;
        Rpc = rpc;
        this.logger = logger;

        ssh.Closed += (_, e) => ended.TrySetResult($"SSH session closed: {e.Reason} {e.Message}".Trim());
        rpc.Completion.ContinueWith(
            t => ended.TrySetResult(t.IsFaulted
                ? $"RPC connection failed: {t.Exception?.GetBaseException().Message}"
                : "RPC connection closed."),
            TaskScheduler.Default);
        rpc.NotificationReceived += OnNotification;
        rpc.RequestHandler = HandleHostRequestAsync;
    }

    public SharedWorkspace Workspace { get; }

    public HostConnection Connection { get; }

    public WorkspaceSessionInfo SessionInfo { get; private set; } = new();

    /// <summary>The host runs on this machine, so its shared ports are already in use or would loop back.</summary>
    public bool IsHostOnThisMachine => HostConnector.IsHostOnThisMachine(Workspace);

    public RpcConnection Rpc { get; }

    /// <summary>Completes with a reason when the session ends.</summary>
    public Task<string> Ended => ended.Task;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (!ssh.IsClosed)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Rpc.InvokeAsync("workspace.unjoinWorkspace", [Workspace.Id], timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogDebug("Leaving the workspace failed: {Message}", ex.Message);
            }
        }

        ended.TrySetResult("Left the session.");
        await Rpc.DisposeAsync();
        await ssh.DisposeAsync();
    }

    public event EventHandler<ServerSharingEventArgs>? ServerSharingStarted;
    public event EventHandler<ServerSharingEventArgs>? ServerSharingStopped;
    public event EventHandler<ServerSharingEventArgs>? ServerSharingChanged;

    public static async Task<GuestSession> JoinAsync(
        GuestJoinOptions options,
        HttpClient httpClient,
        ILoggerFactory loggerFactory,
        TraceSource? sshTrace = null,
        CancellationToken cancellationToken = default)
    {
        var logger = loggerFactory.CreateLogger<GuestSession>();
        var workspaceId = JoinLink.ParseWorkspaceId(options.JoinLink);

        var token = options.Token ??
                    await new LiveShareAuthClient(httpClient, options.Endpoints).GetAnonymousTokenAsync(
                        cancellationToken);
        logger.LogDebug("Using {Token}", token);

        var workspace =
            await new LiveShareServiceClient(httpClient, options.Endpoints).AddGuestAsync(workspaceId, token,
                cancellationToken);
        logger.LogInformation(
            "Workspace {Name} ({Id}): host connected={HostConnected}, mode={Mode}",
            workspace.Name, workspace.Id, workspace.IsHostConnected, workspace.ConnectionMode);
        if (workspace.SessionToken is null)
        {
            throw new LiveShareServiceException("The service did not return a session token for this guest.");
        }

        var connection = await HostConnector.ConnectAsync(workspace, options.ConnectionMode, logger, cancellationToken);
        logger.LogInformation("Connected to the host via {Mode} ({Route})", connection.Mode, connection.Description);

        return await JoinHostAsync(
            workspaceId,
            workspace,
            connection,
            token.IsAnonymous ? options.AnonymousUserName : null,
            options.JoinTimeout,
            loggerFactory,
            sshTrace,
            cancellationToken);
    }

    /// <summary>
    ///     Runs the guest protocol over an established host connection: SSH, version exchange and join.
    /// </summary>
    internal static async Task<GuestSession> JoinHostAsync(
        string workspaceId,
        SharedWorkspace workspace,
        HostConnection connection,
        string? anonymousUserName,
        TimeSpan joinTimeout,
        ILoggerFactory loggerFactory,
        TraceSource? sshTrace,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger<GuestSession>();
        var sessionToken = workspace.SessionToken
                           ?? throw new LiveShareServiceException(
                               "The service did not return a session token for this guest.");

        LiveShareSshSession ssh;
        try
        {
            ssh = await LiveShareSshSession.ConnectAsync(
                connection.Stream, workspace.HostPublicKeys, sessionToken,
                sshTrace ?? new TraceSource($"{ProductInfo.PackageId}.SSH", SourceLevels.Off), cancellationToken);
        }
        catch
        {
            await connection.Stream.DisposeAsync();
            throw;
        }

        var rpc = new RpcConnection(ssh.RpcStream, loggerFactory.CreateLogger<RpcConnection>());
        var session = new GuestSession(workspace, connection, ssh, rpc, logger);
        rpc.Start();
        try
        {
            var hostVersion = await rpc.InvokeAsync("version.exchangeVersions", [new { }], cancellationToken);
            logger.LogDebug("Host version: {Version}", hostVersion);

            logger.LogInformation("Joining the session; the host may need to accept this guest...");
            using var joinCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            joinCancellation.CancelAfter(joinTimeout);
            var joinInfo = new
            {
                id = workspaceId,
                connectionMode = ConnectionMode.Local,
                joiningUserSessionToken = sessionToken,
                clientCapabilities = new { isNonInteractive = false },
                anonymousUserName
            };
            session.SessionInfo =
                await rpc.InvokeAsync<WorkspaceSessionInfo>("workspace.joinWorkspace", [joinInfo],
                    joinCancellation.Token)
                ?? throw new RpcRemoteException(RpcConnection.ServerError, "The host returned no session info.");
            logger.LogInformation("Joined as guest #{SessionNumber}", session.SessionInfo.SessionNumber);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<SharedServer>> GetSharedServersAsync(CancellationToken cancellationToken = default)
    {
        return await Rpc.InvokeAsync<SharedServer[]>("serverSharing.getSharedServers", [], cancellationToken) ?? [];
    }

    /// <summary>
    ///     Opens a byte stream to a shared server: the host connects to its local port and
    ///     relays bytes over a dedicated SSH channel.
    /// </summary>
    public async Task<Stream> OpenSharedServerStreamAsync(SharedServer server,
        CancellationToken cancellationToken = default)
    {
        var streamId = await Rpc.InvokeAsync<string>("streamManager.getStream",
                           [server.StreamName, server.StreamCondition], cancellationToken)
                       ?? throw new IOException(
                           $"The host refused a stream to port {server.SourcePort}; it may no longer be shared.");
        return await ssh.OpenStreamAsync(streamId, cancellationToken)
               ?? throw new IOException($"The host rejected the data channel for port {server.SourcePort}.");
    }

    private Task<object?> HandleHostRequestAsync(RpcNotification request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "version.exchangeVersions":
                return Task.FromResult<object?>(new { version = "1.0" });
            default:
                logger.LogDebug("Unhandled host request {Method} {Params}", request.Method, request.Params);
                throw new RpcRemoteException(RpcConnection.MethodNotFound, $"Method not found: {request.Method}");
        }
    }

    private void OnNotification(object? sender, RpcNotification notification)
    {
        switch (notification.Method)
        {
            case "serverSharing.sharingStarted":
                Raise(ServerSharingStarted, notification);
                break;
            case "serverSharing.sharingStopped":
                Raise(ServerSharingStopped, notification);
                break;
            case "serverSharing.sharingChanged":
                Raise(ServerSharingChanged, notification);
                break;
            case "workspace.userRemoved":
                ended.TrySetResult("The host removed this guest from the session.");
                break;
            case "workspace.userBlocked":
                ended.TrySetResult("The host blocked this guest.");
                break;
            default:
                logger.LogTrace("Ignoring notification {Method}", notification.Method);
                break;
        }
    }

    private void Raise(EventHandler<ServerSharingEventArgs>? handler, RpcNotification notification)
    {
        if (notification.GetEventArgs<ServerSharingEventArgs>() is { Server: not null } args)
        {
            handler?.Invoke(this, args);
        }
    }
}