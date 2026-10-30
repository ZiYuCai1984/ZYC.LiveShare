// ReSharper disable HeuristicUnreachableCode

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
#pragma warning disable IDE0005
using ZYC.CoreToolkit;

#pragma warning disable CS0162


namespace ZYC.LiveShare.Capture;

internal class Program
{
    private static readonly HttpClient HttpClient = new();

    // ReSharper disable InconsistentNaming
    public static string Frp_Linux =>
        "https://github.com/fatedier/frp/releases/download/v0.71.1/frp_0.71.1_linux_amd64.tar.gz";

    public static string Frp_Windows =>
        "https://github.com/fatedier/frp/releases/download/v0.71.1/frp_0.71.1_windows_amd64.zip";

    public static string Frp_Macos =>
        "https://github.com/fatedier/frp/releases/download/v0.71.1/frp_0.71.1_darwin_arm64.tar.gz";

#if CAPTURE_WINDOWS
    public static string DownloadUrl => Frp_Windows;
#elif CAPTURE_MACOS
    public static string DownloadUrl => Frp_Macos;
#elif CAPTURE_UBUNTU
    public static string DownloadUrl => Frp_Linux;
#else
    public static string DownloadUrl => OperatingSystem.IsWindows() ? Frp_Windows
        : OperatingSystem.IsMacOS() ? Frp_Macos
        : OperatingSystem.IsLinux() ? Frp_Linux
        : throw new PlatformNotSupportedException();
#endif

#if CAPTURE_WINDOWS
    private const string FrpcToml = """"
                                    serverAddr = "127.0.0.1"
                                    serverPort = 7657
                                    loginFailExit = false
                                    
                                    [[proxies]]
                                    name = "my-service-tcp" 
                                    type = "tcp"            
                                    localIP = "127.0.0.1"
                                    localPort = 3389
                                    remotePort = 5677


                                    """";
#else
    private const string FrpcToml = """"
                                    serverAddr = "127.0.0.1"
                                    serverPort = 7657
                                    loginFailExit = false
                                    
                                    [[proxies]]
                                    name = "my-service-tcp" 
                                    type = "tcp"            
                                    localIP = "127.0.0.1"
                                    localPort = 22
                                    remotePort = 5677
                                    
                                    """";
#endif

    private static async Task<string> PrepareFrpAsync(string downloadUrl)
    {
        var frpDirectory = Path.Combine(AppContext.BaseDirectory, "frp");
        Directory.CreateDirectory(frpDirectory);

        var archiveName = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
        var archivePath = Path.Combine(frpDirectory, archiveName);

        Console.WriteLine($"Downloading frp: {downloadUrl}");
        await using (var download = await HttpClient.GetStreamAsync(downloadUrl))
        await using (var archive = File.Create(archivePath))
        {
            await download.CopyToAsync(archive);
        }

        var isZip = archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var packageName = isZip ? archiveName[..^4] : archiveName[..^7];
        if (isZip)
        {
            ZipFile.ExtractToDirectory(archivePath, frpDirectory, true);
        }
        else
        {
            await using var archive = File.OpenRead(archivePath);
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, frpDirectory, true);
        }

        var packageDirectory = Path.Combine(frpDirectory, packageName);
        var configPath = Path.Combine(packageDirectory, "frpc.toml");
        await File.WriteAllTextAsync(configPath, FrpcToml);
        Console.WriteLine($"frpc.toml written to: {configPath}");
        return packageDirectory;
    }


    // ReSharper disable once UnusedParameter.Local
    private static async Task<int> Main(string[] args)
    {

#if CAPTURE_WINDOWS

        await TempTools.DisableFirewallAsync();

        var rdpUser = Environment.UserName;
        const string rdpPassword = "123456";
        var policyPath = Path.Combine(Path.GetTempPath(), $"zyc-capture-password-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync($"{policyPath}.inf", """
            [Unicode]
            Unicode=yes
            [Version]
            signature="$CHICAGO$"
            Revision=1
            [System Access]
            PasswordComplexity = 0
            MinimumPasswordLength = 6
            """, System.Text.Encoding.Unicode);

        foreach (var (command, arguments) in new[]
        {
            ("REG", """ADD "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v fDenyTSConnections /t REG_DWORD /d 0 /f"""),
            ("secedit", $"/configure /db \"{policyPath}.sdb\" /cfg \"{policyPath}.inf\" /areas SECURITYPOLICY /log \"{policyPath}.log\" /quiet"),
            ("net", $"user \"{rdpUser}\" \"{rdpPassword}\" /active:yes"),
            ("powershell", "-NoProfile -NonInteractive -Command \"$ErrorActionPreference = 'Stop'; Set-Service TermService -StartupType Automatic; Restart-Service TermService -Force\"")
        })
        {
            var exitCode = await CommandTools.ExecuteCommandAsync(command, arguments);
            if (exitCode != 0)
            {
                Console.Error.WriteLine($"{command} failed with exit code {exitCode}.");
                return exitCode;
            }
        }
        Console.WriteLine($"RDP configured for user {Environment.MachineName}\\{rdpUser} on port 3389.");

#elif CAPTURE_MACOS
        const string sshUser = "zyc";
        const string sshPassword = "123456";
        using (var sshSetupProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "sudo",
            ArgumentList =
            {
                "-n", "/bin/bash", "-c",
                """
                set -euo pipefail
                IFS= read -r ssh_user
                IFS= read -r ssh_password
                if ! /usr/bin/dscl . -read "/Users/$ssh_user" >/dev/null 2>&1; then
                    /usr/sbin/sysadminctl -addUser "$ssh_user" -fullName "Live Share Capture" \
                        -password "$ssh_password" -shell /bin/zsh -home "/Users/$ssh_user" -admin
                fi
                if ! /usr/bin/dscl . -authonly "$ssh_user" "$ssh_password"; then
                    printf 'SSH password verification failed for user %s.\n' "$ssh_user" >&2
                    exit 1
                fi

                mkdir -p /etc/ssh/sshd_config.d
                printf '%s\n' 'PasswordAuthentication yes' 'UsePAM yes' > /etc/ssh/sshd_config.d/00-capture.conf
                if /usr/bin/dscl . -read /Groups/com.apple.access_ssh >/dev/null 2>&1; then
                    /usr/sbin/dseditgroup -o edit -a "$ssh_user" -t user com.apple.access_ssh
                fi

                /usr/bin/ssh-keygen -A
                /usr/sbin/sshd -t
                /bin/launchctl enable system/com.openssh.sshd
                if ! /bin/launchctl print system/com.openssh.sshd >/dev/null 2>&1; then
                    /bin/launchctl bootstrap system /System/Library/LaunchDaemons/ssh.plist
                fi
                /usr/bin/nc -z -w 5 127.0.0.1 22
                """
            },
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start SSH setup."))
        {
            await sshSetupProcess.StandardInput.WriteLineAsync(sshUser);
            await sshSetupProcess.StandardInput.WriteLineAsync(sshPassword);
            sshSetupProcess.StandardInput.Close();
            await sshSetupProcess.WaitForExitAsync();
            if (sshSetupProcess.ExitCode != 0)
            {
                Console.Error.WriteLine($"macOS SSH setup failed with exit code {sshSetupProcess.ExitCode}.");
                return sshSetupProcess.ExitCode;
            }
        }
        Console.WriteLine($"SSH is ready for user {sshUser} on port 22.");

#elif CAPTURE_UBUNTU
        const string sshPassword = "123456";
        using (var sshSetupProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "sudo",
            ArgumentList =
            {
                "-n", "bash", "-c",
                """
                set -euo pipefail
                chpasswd
                printf '%s\n' 'PasswordAuthentication yes' > /etc/ssh/sshd_config.d/00-capture.conf
                /usr/sbin/sshd -t
                systemctl restart ssh
                """
            },
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start SSH setup."))
        {
            await sshSetupProcess.StandardInput.WriteLineAsync($"runner:{sshPassword}");
            sshSetupProcess.StandardInput.Close();
            await sshSetupProcess.WaitForExitAsync();
            if (sshSetupProcess.ExitCode != 0)
            {
                throw new InvalidOperationException($"SSH setup failed with exit code {sshSetupProcess.ExitCode}.");
            }
        }
        Console.WriteLine("SSH is ready for user runner on port 22.");

#else

        throw new InvalidOperationException();
#endif


        var frpDirectory = await PrepareFrpAsync(DownloadUrl);
        using var frpcProcess = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(frpDirectory, OperatingSystem.IsWindows() ? "frpc.exe" : "frpc"),
            WorkingDirectory = frpDirectory,
            ArgumentList = { "-c", "frpc.toml" },
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start frpc.");
        Console.WriteLine($"frpc started (PID: {frpcProcess.Id}).");

        var joinLink = Environment.GetEnvironmentVariable("JOIN_LINK");

        if (string.IsNullOrWhiteSpace(joinLink))
        {
            throw new InvalidOperationException();
        }

        var options = CliOptions.Parse(["join", joinLink, "--name", "zyc"]);

        return await LiveShare.Program.RunAsync(options, HttpClient);
#pragma warning restore CS0162 // Unreachable code detected
    }
}
