using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Azure.Relay;
using Microsoft.Extensions.Logging;
using ZYC.LiveShare.Service;

namespace ZYC.LiveShare.Transport;

/// <summary>
///     Opens a raw byte stream to the host. Like the desktop Live Share client, it races the host's
///     direct addresses against the Azure Relay hybrid connection and keeps the first that connects.
/// </summary>
public static class HostConnector
{
    private const int RelayAttempts = 4;
    private static readonly TimeSpan DirectConnectTimeout = TimeSpan.FromSeconds(2);

    public static async Task<HostConnection> ConnectAsync(
        SharedWorkspace workspace, ConnectionMode requestedMode, ILogger logger, CancellationToken cancellationToken)
    {
        var mode = ResolveMode(requestedMode, workspace.ConnectionMode);
        using var raceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var attempts = new List<Task<HostConnection>>();
        if (mode is ConnectionMode.Auto or ConnectionMode.Direct)
        {
            foreach (var link in workspace.ConnectLinks)
            {
                if (TryParseDirectAddress(link, out var host, out var port))
                {
                    attempts.Add(ConnectDirectAsync(host, port, logger, raceCancellation.Token));
                }
            }
        }

        if (mode is ConnectionMode.Auto or ConnectionMode.Relay && workspace.RelayLink is { } relayLink &&
            workspace.RelaySas is { } relaySas)
        {
            attempts.Add(ConnectRelayAsync(new Uri(relayLink), relaySas, logger, raceCancellation.Token));
        }

        if (attempts.Count == 0)
        {
            var reason = workspace.TunnelProperties is not null
                ? "the host only offers a dev tunnel, which is not supported"
                : $"the workspace has no usable connection info for mode '{mode}'";
            throw new HostConnectionException($"Cannot connect to the host: {reason}.");
        }

        var failures = new List<Exception>();
        var pending = attempts.ToList();
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending);
            pending.Remove(finished);
            if (finished.IsCompletedSuccessfully)
            {
                raceCancellation.Cancel();
                DisposeWhenDone(pending);
                return finished.Result;
            }

            failures.Add(finished.Exception?.GetBaseException() ?? new OperationCanceledException());
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new HostConnectionException("Could not connect to the host by any route.",
            new AggregateException(failures));
    }

    internal static ConnectionMode ResolveMode(ConnectionMode requested, ConnectionMode hostMode)
    {
        if (hostMode is ConnectionMode.Auto || hostMode == requested)
        {
            return requested;
        }

        return requested switch
        {
            ConnectionMode.Auto => hostMode,
            ConnectionMode.Direct when hostMode is ConnectionMode.Relay =>
                throw new HostConnectionException(
                    "The host does not allow direct connections. Use auto or relay mode."),
            ConnectionMode.Relay when hostMode is ConnectionMode.Direct =>
                throw new HostConnectionException(
                    "The host does not allow relay connections. Use auto or direct mode."),
            _ => requested
        };
    }

    /// <summary>
    ///     Whether any of the host's direct addresses belongs to this machine. Forwarding a shared
    ///     port to the same local port would then loop back into the forwarder itself.
    /// </summary>
    public static bool IsHostOnThisMachine(SharedWorkspace workspace)
    {
        var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .ToHashSet();
        return workspace.ConnectLinks.Any(link =>
            TryParseDirectAddress(link, out var host, out _)
            && IPAddress.TryParse(host.Trim('[', ']'), out var address)
            && (IPAddress.IsLoopback(address) || localAddresses.Contains(address)));
    }

    /// <summary>Direct addresses look like <c>net.tcp://host:port/</c>.</summary>
    internal static bool TryParseDirectAddress(string address, out string host, out int port)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "net.tcp" && !uri.IsDefaultPort &&
            uri.PathAndQuery == "/")
        {
            host = uri.Host;
            port = uri.Port;
            return true;
        }

        host = string.Empty;
        port = 0;
        return false;
    }

    private static async Task<HostConnection> ConnectDirectAsync(string host, int port, ILogger logger,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DirectConnectTimeout);
        var client = new TcpClient { NoDelay = true };
        try
        {
            logger.LogDebug("Trying direct connection to {Host}:{Port}", host, port);
            await client.ConnectAsync(host, port, timeout.Token);
            logger.LogDebug("Direct connection to {Host}:{Port} established", host, port);
            return new HostConnection(client.GetStream(), ConnectionMode.Direct, $"{host}:{port}");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<HostConnection> ConnectRelayAsync(Uri relayUri, string sas, ILogger logger,
        CancellationToken cancellationToken)
    {
        var client = new HybridConnectionClient(relayUri, TokenProvider.CreateSharedAccessSignatureTokenProvider(sas))
        {
            OperationTimeout = TimeSpan.FromSeconds(100)
        };

        for (var attempt = 1;; attempt++)
        {
            try
            {
                logger.LogDebug("Trying relay connection to {Relay} (attempt {Attempt})", relayUri.Host, attempt);
                var connecting = client.CreateConnectionAsync();
                HybridConnectionStream stream;
                try
                {
                    stream = await connecting.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // The relay client cannot be cancelled; close the connection if it completes later.
                    _ = connecting.ContinueWith(
                        t => t.Result.Dispose(),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnRanToCompletion,
                        TaskScheduler.Default);
                    throw;
                }

                logger.LogDebug("Relay connection to {Relay} established", relayUri.Host);
                return new HostConnection(stream, ConnectionMode.Relay, relayUri.Host);
            }
            catch (Exception ex) when (attempt < RelayAttempts && ex is EndpointNotFoundException or RelayException &&
                                       !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Relay connection attempt {Attempt} failed: {Message}", attempt, ex.Message);
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private static void DisposeWhenDone(IEnumerable<Task<HostConnection>> losers)
    {
        foreach (var loser in losers)
        {
            _ = loser.ContinueWith(
                t => t.Result.Stream.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }
    }
}

public sealed record HostConnection(Stream Stream, ConnectionMode Mode, string Description);

public sealed class HostConnectionException(string message, Exception? innerException = null)
    : Exception(message, innerException);