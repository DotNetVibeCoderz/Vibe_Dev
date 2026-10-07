using System.Text;
using Marbots.Abstractions;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace Marbots.Runtime;

/// <summary>
/// SSH bootstrap of a remote agent host: preflight (OS, architecture, can it reach this server?), upload the
/// self-contained marbots-host binary, enroll it with a one-time token, and install it to start at logon (Windows
/// scheduled task in the user's interactive session, so desktop/computer-use tools work; systemd user unit on Linux).
/// The SSH password or key is used only for this call and never stored.
/// </summary>
public sealed class HostBootstrapper(MarbotsOptions options, HostRegistry registry, HostConnectionManager connections, ILogger<HostBootstrapper> log)
{
    public const string LaunchdLabel = "id.gravicode.marbots-host";

    public const string TaskName = "Marbots Host";

    public string PackagesDirectory => options.HostPackagesDirectory is { Length: > 0 } d ? Path.GetFullPath(d) : options.DataPath("host-packages");

    public string? PackageFor(string rid)
    {
        foreach (var dir in new[] { PackagesDirectory, Path.Combine(AppContext.BaseDirectory, "host-packages") })
            foreach (var name in new[] { $"marbots-host-{rid}.exe", $"marbots-host-{rid}" })
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    public async Task<SshBootstrapResult> BootstrapAsync(SshBootstrapRequest req, string by, CancellationToken ct)
    {
        var result = new SshBootstrapResult();
        void Step(string s) { result.Log.Add(s); log.LogInformation("Bootstrap {Host}: {Step}", req.Host, s); }
        try
        {
            if (string.IsNullOrWhiteSpace(req.Host) || string.IsNullOrWhiteSpace(req.User)) throw new ArgumentException("Host and user are required.");
            if (!Uri.TryCreate(req.ServerUrl, UriKind.Absolute, out var given)) throw new ArgumentException("ServerUrl must be the address the new host uses to reach this server, e.g. http://192.168.1.10:5170");
            // A tenant's hosts talk to /t/<tenant>/…; the trailing slash keeps relative paths under it.
            var server = new Uri(Tenants.ServerUrlFor(given.AbsoluteUri, options.TenantId) + "/");
            var auth = new List<AuthenticationMethod>();
            if (!string.IsNullOrEmpty(req.Password)) auth.Add(new PasswordAuthenticationMethod(req.User, req.Password));
            if (!string.IsNullOrEmpty(req.PrivateKey))
            {
                using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(req.PrivateKey));
                auth.Add(new PrivateKeyAuthenticationMethod(req.User, new PrivateKeyFile(keyStream)));
            }
            if (auth.Count == 0) throw new ArgumentException("A password or private key is required for the bootstrap.");
            var info = new ConnectionInfo(req.Host, req.Port, req.User, [.. auth]) { Timeout = TimeSpan.FromSeconds(20) };

            using var ssh = new SshClient(info);
            await ssh.ConnectAsync(ct);
            Step($"Connected to {req.User}@{req.Host}:{req.Port}.");

            // ---- preflight
            var uname = Run(ssh, "uname -sm");
            var windows = uname.Exit != 0 || uname.Output.Contains("windows", StringComparison.OrdinalIgnoreCase) || uname.Output.Contains("MINGW", StringComparison.Ordinal);
            string rid, home, exe;
            if (windows)
            {
                var arch = Run(ssh, "echo %PROCESSOR_ARCHITECTURE%").Output.Trim();
                rid = arch.Contains("ARM", StringComparison.OrdinalIgnoreCase) ? "win-arm64" : "win-x64";
                home = Run(ssh, "echo %LOCALAPPDATA%").Output.Trim();
                Step($"Windows ({Run(ssh, "ver").Output.Trim()}), {arch}.");
                exe = home + @"\Marbots\Host\marbots-host.exe";
            }
            else
            {
                var parts = uname.Output.Trim().Split(' ');
                var arm = parts.Length > 1 && parts[1] is "aarch64" or "arm64";
                rid = parts[0] == "Darwin" ? (arm ? "osx-arm64" : "osx-x64") : arm ? "linux-arm64" : "linux-x64";
                home = Run(ssh, "printf %s \"$HOME\"").Output.Trim();
                Step($"{uname.Output.Trim()}.");
                exe = home + "/.local/share/marbots/host/marbots-host";
            }
            var reach = windows
                ? Run(ssh, $"powershell -NoProfile -Command \"try {{ (Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 '{server}api/v1/system').StatusCode }} catch {{ if ($_.Exception.Response) {{ [int]$_.Exception.Response.StatusCode }} else {{ 'unreachable: ' + $_.Exception.Message }} }}\"")
                : Run(ssh, $"curl -s -o /dev/null -w '%{{http_code}}' --max-time 10 '{server}api/v1/system' || echo unreachable");
            var code = reach.Output.Trim();
            if (code is not ("200" or "401")) throw new InvalidOperationException($"The host cannot reach {server} ({code}). Use this server's LAN address and allow it through the firewall.");
            Step($"The host can reach {server}.");

            var package = PackageFor(rid) ?? throw new InvalidOperationException(
                $"No agent-host package for {rid}. Build it with: dotnet publish src/Marbots.AgentHost -c Release -r {rid} -o {PackagesDirectory} then rename to marbots-host-{rid}{(windows ? ".exe" : "")}.");

            // ---- stop an older copy, upload
            if (windows)
            {
                Run(ssh, $"schtasks /End /TN \"{TaskName}\"");
                Run(ssh, "taskkill /F /IM marbots-host.exe");
                Run(ssh, $"if not exist \"{home}\\Marbots\\Host\" mkdir \"{home}\\Marbots\\Host\"");
            }
            else
            {
                Run(ssh, $"systemctl --user stop marbots-host 2>/dev/null; launchctl bootout gui/$(id -u)/{LaunchdLabel} 2>/dev/null; pkill -f 'marbots-host run' 2>/dev/null; true");
                Run(ssh, $"mkdir -p '{Path.GetDirectoryName(exe)!.Replace('\\', '/')}'");
            }
            using (var sftp = new SftpClient(info))
            {
                await sftp.ConnectAsync(ct);
                var remotePath = windows ? "/" + exe.Replace('\\', '/') : exe;
                await using var file = File.OpenRead(package);
                await Task.Run(() => sftp.UploadFile(file, remotePath, true), ct);
                // SSH.NET takes the octal digits written as a decimal number.
                if (!windows) sftp.ChangePermissions(remotePath, 755);
                sftp.Disconnect();
            }
            Step($"Uploaded {Path.GetFileName(package)} ({new FileInfo(package).Length / 1024 / 1024} MB).");
            var mac = rid.StartsWith("osx", StringComparison.Ordinal);
            if (mac)
            {
                // Apple silicon refuses unsigned code: sign ad hoc, and drop any quarantine flag.
                Run(ssh, $"xattr -c '{exe}' 2>/dev/null; codesign --force -s - '{exe}' 2>/dev/null; true");
            }

            if (req.UpdateOnly)
            {
                if (windows) Run(ssh, $"schtasks /Run /TN \"{TaskName}\"");
                else if (mac) Run(ssh, $"launchctl kickstart -k gui/$(id -u)/{LaunchdLabel} 2>/dev/null || (nohup '{exe}' run > ~/.local/share/marbots/host/nohup.log 2>&1 &)");
                else Run(ssh, "systemctl --user restart marbots-host 2>/dev/null || (nohup ~/.local/share/marbots/host/marbots-host run > ~/.local/share/marbots/host/nohup.log 2>&1 &)");
                Step("Updated the binary and restarted the host (enrollment kept).");
                result.Success = true;
                ssh.Disconnect();
                return result;
            }

            // ---- enroll with a one-time token
            var name = string.IsNullOrWhiteSpace(req.Name) ? req.Host : req.Name;
            var (token, _) = await registry.CreateEnrollmentAsync(name, TimeSpan.FromMinutes(10), by, ct);
            var enroll = Run(ssh, windows ? $"\"{exe}\" enroll --server {server} --token {token} --name \"{name}\"" : $"'{exe}' enroll --server {server} --token {token} --name '{name}'");
            if (enroll.Exit != 0) throw new InvalidOperationException("Enrollment failed: " + enroll.Output);
            var hostId = enroll.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(w => w != "as").Skip(1).FirstOrDefault()?.Trim();
            Step($"Enrolled as {hostId}.");
            if (hostId is not null && await registry.GetAsync(hostId, ct) is { } record)
            {
                record.InstalledVia = $"ssh {req.User}@{req.Host}";
                await registry.SaveAsync(record, ct);
            }

            // ---- start at logon, start now
            if (windows)
            {
                var create = Run(ssh, $"schtasks /Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" run\" /SC ONLOGON /RL LIMITED /IT /F");
                if (create.Exit != 0) throw new InvalidOperationException("Could not create the logon task: " + create.Output);
                Run(ssh, $"schtasks /Run /TN \"{TaskName}\"");
                Step("Installed the logon task 'Marbots Host' (runs in the user's desktop session) and started it.");
            }
            else if (mac)
            {
                // A launchd agent: starts at login, restarts if it stops.
                var plist = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\"><dict>"
                    + $"<key>Label</key><string>{LaunchdLabel}</string><key>ProgramArguments</key><array><string>{exe}</string><string>run</string></array>"
                    + "<key>RunAtLoad</key><true/><key>KeepAlive</key><true/>"
                    + $"<key>StandardOutPath</key><string>{home}/.local/share/marbots/host/launchd.log</string><key>StandardErrorPath</key><string>{home}/.local/share/marbots/host/launchd.log</string>"
                    + "</dict></plist>\n";
                var plistPath = $"{home}/Library/LaunchAgents/{LaunchdLabel}.plist";
                Run(ssh, $"mkdir -p ~/Library/LaunchAgents && printf '%s' '{plist.Replace("'", "'\\''")}' > '{plistPath}'");
                Run(ssh, $"launchctl bootout gui/$(id -u)/{LaunchdLabel} 2>/dev/null; true");
                var ld = Run(ssh, $"launchctl bootstrap gui/$(id -u) '{plistPath}' 2>&1 || launchctl load -w '{plistPath}' 2>&1");
                await Task.Delay(1500, ct);
                var running = Run(ssh, "pgrep -f 'marbots-host run' >/dev/null && echo yes || echo no").Output.Trim() == "yes";
                if (!running) Run(ssh, $"nohup '{exe}' run > ~/.local/share/marbots/host/nohup.log 2>&1 &");
                Step(running ? $"Installed the launchd agent {LaunchdLabel} (starts at login) and started it."
                    : "No GUI login session for launchd (it starts at the next login); started now with nohup.");
            }
            else
            {
                var unit = $"[Unit]\nDescription=Marbots agent host\nAfter=network-online.target\n\n[Service]\nExecStart={exe} run\nRestart=always\nRestartSec=5\n\n[Install]\nWantedBy=default.target\n";
                Run(ssh, $"mkdir -p ~/.config/systemd/user && printf '%s' '{unit.Replace("'", "'\\''")}' > ~/.config/systemd/user/marbots-host.service");
                var sd = Run(ssh, "systemctl --user daemon-reload && systemctl --user enable --now marbots-host && (loginctl enable-linger \"$USER\" 2>/dev/null || true)");
                if (sd.Exit != 0) Run(ssh, $"nohup '{exe}' run > ~/.local/share/marbots/host/nohup.log 2>&1 &");
                Step(sd.Exit == 0 ? "Installed the systemd user service marbots-host." : "systemd user services unavailable; started with nohup.");
            }

            // ---- wait for it
            for (var i = 0; i < 40 && hostId is not null && !connections.IsOnline(hostId); i++) await Task.Delay(1000, ct);
            if (hostId is not null && connections.IsOnline(hostId))
            {
                Step("The host is online.");
                result.Success = true;
            }
            else
            {
                var session = windows ? Run(ssh, "query user").Output : "";
                Step(windows && !session.Contains("Active", StringComparison.OrdinalIgnoreCase)
                    ? "Installed, but nobody is logged on to the desktop; the host starts at the next logon."
                    : "Installed, but the host has not connected yet. Check the host log (%LOCALAPPDATA%\\Marbots\\Host\\host.log).");
                result.Success = true;
            }
            result.HostId = hostId;
            ssh.Disconnect();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or IOException or TimeoutException)
        {
            result.Error = ex.Message;
            result.Log.Add("Failed: " + ex.Message);
        }
        return result;
    }

    private static (int Exit, string Output) Run(SshClient ssh, string command)
    {
        using var cmd = ssh.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromMinutes(2);
        var stdout = cmd.Execute();
        return (cmd.ExitStatus ?? -1, stdout + cmd.Error);
    }
}
