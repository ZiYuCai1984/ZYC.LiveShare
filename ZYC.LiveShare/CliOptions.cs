using System.Net;
using ZYC.LiveShare.Service;
namespace ZYC.LiveShare;

public sealed class CliOptions
{
    public string Command { get; private set; } = "";
    public string? Target { get; private set; }

    //!WARNING Try Get from LIVESHARE_TOKEN and LIVESHARE_GITHUB_TOKEN
    public string? Token { get; private set; } = Environment.GetEnvironmentVariable("LIVESHARE_TOKEN");
    public string? GitHubToken { get; private set; } = Environment.GetEnvironmentVariable("LIVESHARE_GITHUB_TOKEN");
    public string? Name { get; private set; }
    public ConnectionMode Mode { get; private set; } = ConnectionMode.Auto;
    public IPAddress Bind { get; private set; } = IPAddress.Loopback;
    public Dictionary<int, int> PortMap { get; } = [];
    public int Verbosity { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions { Command = args[0] };
        if (options.Command is not ("join" or "token"))
        {
            throw new FormatException($"Unknown command '{options.Command}'.");
        }

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];

            string Value()
            {
                return ++i < args.Length ? args[i] : throw new FormatException($"Option {arg} needs a value.");
            }

            switch (arg)
            {
                case "--github-token": options.GitHubToken = Value(); break;
                case "--token": options.Token = Value(); break;
                case "--name": options.Name = Value(); break;
                case "--mode":
                    options.Mode = Value().ToLowerInvariant() switch
                    {
                        "auto" => ConnectionMode.Auto,
                        "direct" => ConnectionMode.Direct,
                        "relay" => ConnectionMode.Relay,
                        var other => throw new FormatException($"Unknown mode '{other}'.")
                    };
                    break;
                case "--bind":
                    options.Bind = IPAddress.TryParse(Value(), out var address)
                        ? address
                        : throw new FormatException("Invalid --bind address.");
                    break;
                case "--map":
                    var parts = Value().Split(':');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out var hostPort) ||
                        !int.TryParse(parts[1], out var localPort))
                    {
                        throw new FormatException("--map expects <host-port>:<local-port>.");
                    }

                    options.PortMap[hostPort] = localPort;
                    break;
                case "-v" or "--verbose": options.Verbosity++; break;
                case "-vv": options.Verbosity += 2; break;
                default:
                    if (arg.StartsWith('-') || options.Target is not null)
                    {
                        throw new FormatException($"Unexpected argument '{arg}'.");
                    }

                    options.Target = arg;
                    break;
            }
        }

        if (options.Command == "join" && options.Target is null)
        {
            throw new FormatException("join needs a join link or workspace id.");
        }

        return options;
    }
}