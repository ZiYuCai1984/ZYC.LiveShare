using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ZYC.LiveShare.Auth;
using ZYC.LiveShare.Guest;
using ZYC.LiveShare.Service;

namespace ZYC.LiveShare;

public class Program
{
    private const string Usage = """
                                 Usage:
                                   lstunnel join <join-link|workspace-id> [options]   Join as a guest and forward the host's shared servers
                                   lstunnel token [options]                           Acquire a Live Share token and show who it belongs to

                                 Options:
                                   --github-token <token>  Sign in by exchanging a GitHub OAuth token (env: LIVESHARE_GITHUB_TOKEN)
                                   --token <jwt>           Use an existing Live Share token (env: LIVESHARE_TOKEN)
                                                           Without either, joins anonymously (the host must allow anonymous guests).
                                   --name <name>           Display name for an anonymous guest
                                   --mode <auto|direct|relay>  Connection mode (default: auto)
                                   --bind <address>        Local address to listen on (default: 127.0.0.1)
                                   --map <host>:<local>    Forward host port <host> to local port <local> (repeatable)
                                   -v, --verbose           Debug logging; -vv also logs every RPC message and SSH trace
                                 """;

    private static async Task<CascadeToken?> AcquireTokenAsync(CliOptions options, LiveShareAuthClient auth,
        CancellationToken cancellationToken)
    {
        if (options.Token is { } jwt)
        {
            return CascadeToken.Parse(jwt);
        }

        return options.GitHubToken is { } gitHubToken
            ? await auth.ExchangeTokenAsync("github", gitHubToken, cancellationToken)
            : null;
    }

    private static async Task<int> JoinAsync(
        CliOptions options, CascadeToken? token, HttpClient httpClient, ILoggerFactory loggerFactory, ILogger logger,
        CancellationToken cancellationToken)
    {
        TraceSource? sshTrace = null;
        if (options.Verbosity > 1)
        {
            sshTrace = new TraceSource($"{ProductInfo.PackageId}.SSH", SourceLevels.Verbose);
            sshTrace.Listeners.Add(new ConsoleTraceListener(true));
        }

        await using var session = await GuestSession.JoinAsync(
            new GuestJoinOptions
            {
                JoinLink = options.Target!,
                Token = token,
                AnonymousUserName = options.Name,
                ConnectionMode = options.Mode
            },
            httpClient,
            loggerFactory,
            sshTrace,
            cancellationToken);

        // Listening on the host's own port numbers would intercept the host's connections to them.
        var avoidHostPorts = session.IsHostOnThisMachine;
        if (avoidHostPorts)
        {
            logger.LogWarning(
                "The host runs on this machine; unmapped ports are forwarded to free local ports instead.");
        }

        await using var forwarder = new PortForwarder(
            session,
            new PortForwarderOptions
            {
                BindAddress = options.Bind,
                MapPort = port => options.PortMap.TryGetValue(port, out var local) ? local : avoidHostPorts ? 0 : port
            },
            loggerFactory.CreateLogger<PortForwarder>());
        await forwarder.StartAsync(cancellationToken);

        if (forwarder.ForwardedPorts.Count == 0)
        {
            logger.LogInformation("The host is not sharing any servers yet; waiting for it to share one.");
        }

        logger.LogInformation("Press Ctrl+C to leave the session.");
        var ended = await session.Ended.WaitAsync(cancellationToken).ContinueWith(
            t => t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default);
        if (ended is not null)
        {
            logger.LogWarning("Session ended: {Reason}", ended);
            return 1;
        }

        logger.LogInformation("Leaving the session...");
        return 0;
    }


    internal static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }


        var httpClient = new HttpClient();
        return await RunAsync(options, httpClient);
    }


    public static async Task<int> RunAsync(CliOptions options, HttpClient httpClient)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            })
            .SetMinimumLevel(options.Verbosity switch
            {
                0 => LogLevel.Information,
                1 => LogLevel.Debug,
                _ => LogLevel.Trace
            })
            .AddFilter("System.Net.Http", LogLevel.Warning));
        var logger = loggerFactory.CreateLogger("lstunnel");

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            // ReSharper disable once AccessToDisposedClosure
            cancellation.Cancel();
        };

        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"{ProductInfo.PackageId}/{ProductInfo.Version}");
        var endpoints = LiveShareEndpoints.Production;

        try
        {
            var token = await AcquireTokenAsync(options, new LiveShareAuthClient(httpClient, endpoints),
                cancellation.Token);
            if (options.Command == "token")
            {
                var resolved = token ??
                               await new LiveShareAuthClient(httpClient, endpoints).GetAnonymousTokenAsync(cancellation
                                   .Token);
                var profile =
                    await new LiveShareServiceClient(httpClient, endpoints).GetProfileAsync(resolved,
                        cancellation.Token);
                Console.WriteLine($"{resolved}");
                Console.WriteLine(
                    $"Profile: {profile.Name} <{profile.Email}> via {profile.Provider} (id {profile.Id})");
                return 0;
            }

            return await JoinAsync(options, token, httpClient, loggerFactory, logger, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception ex)
        {
            logger.LogError("{Message}", options.Verbosity > 0 ? ex.ToString() : ex.Message);
            return 1;
        }
    }
}