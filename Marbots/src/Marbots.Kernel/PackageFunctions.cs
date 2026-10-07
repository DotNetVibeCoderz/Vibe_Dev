using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Marbots.Abstractions;

namespace Marbots.Kernel;

/// <summary>
/// Installs prerequisites a task needs (python, dotnet, node, git, …) with the machine's package manager: winget /
/// choco / scoop on Windows, apt / dnf / yum / pacman / apk / zypper on Linux, brew on macOS. Prefers per-user installs
/// that need no administrator rights; the .NET SDK uses Microsoft's dotnet-install script into the user profile.
/// </summary>
public static class Prerequisites
{
    /// <summary>Common tool names → package id per manager. Anything else is passed to the manager as-is.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalog = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = Map(("winget", "Python.Python.3.13"), ("choco", "python"), ("scoop", "python"), ("apt", "python3 python3-pip python3-venv"), ("dnf", "python3 python3-pip"), ("yum", "python3"), ("pacman", "python python-pip"), ("apk", "python3 py3-pip"), ("zypper", "python3"), ("brew", "python")),
        ["node"] = Map(("winget", "OpenJS.NodeJS.LTS"), ("choco", "nodejs-lts"), ("scoop", "nodejs-lts"), ("apt", "nodejs npm"), ("dnf", "nodejs npm"), ("yum", "nodejs"), ("pacman", "nodejs npm"), ("apk", "nodejs npm"), ("zypper", "nodejs"), ("brew", "node")),
        ["git"] = Map(("winget", "Git.Git"), ("choco", "git"), ("scoop", "git"), ("apt", "git"), ("dnf", "git"), ("yum", "git"), ("pacman", "git"), ("apk", "git"), ("zypper", "git"), ("brew", "git")),
        ["java"] = Map(("winget", "Microsoft.OpenJDK.21"), ("choco", "openjdk"), ("scoop", "openjdk"), ("apt", "openjdk-21-jdk"), ("dnf", "java-21-openjdk-devel"), ("yum", "java-21-openjdk-devel"), ("pacman", "jdk-openjdk"), ("apk", "openjdk21"), ("zypper", "java-21-openjdk-devel"), ("brew", "openjdk@21")),
        ["go"] = Map(("winget", "GoLang.Go"), ("choco", "golang"), ("scoop", "go"), ("apt", "golang"), ("dnf", "golang"), ("yum", "golang"), ("pacman", "go"), ("apk", "go"), ("zypper", "go"), ("brew", "go")),
        ["rust"] = Map(("winget", "Rustlang.Rustup"), ("choco", "rustup.install"), ("scoop", "rustup"), ("apt", "rustc cargo"), ("dnf", "rust cargo"), ("yum", "rust cargo"), ("pacman", "rust"), ("apk", "rust cargo"), ("zypper", "rust"), ("brew", "rust")),
        ["ffmpeg"] = Map(("winget", "Gyan.FFmpeg"), ("choco", "ffmpeg"), ("scoop", "ffmpeg"), ("apt", "ffmpeg"), ("dnf", "ffmpeg"), ("yum", "ffmpeg"), ("pacman", "ffmpeg"), ("apk", "ffmpeg"), ("zypper", "ffmpeg"), ("brew", "ffmpeg")),
        ["7zip"] = Map(("winget", "7zip.7zip"), ("choco", "7zip"), ("scoop", "7zip"), ("apt", "p7zip-full"), ("dnf", "p7zip"), ("pacman", "p7zip"), ("apk", "p7zip"), ("brew", "p7zip")),
        ["pandoc"] = Map(("winget", "JohnMacFarlane.Pandoc"), ("choco", "pandoc"), ("scoop", "pandoc"), ("apt", "pandoc"), ("dnf", "pandoc"), ("pacman", "pandoc"), ("apk", "pandoc"), ("brew", "pandoc")),
        ["libreoffice"] = Map(("winget", "TheDocumentFoundation.LibreOffice"), ("choco", "libreoffice-fresh"), ("scoop", "libreoffice"), ("apt", "libreoffice"), ("dnf", "libreoffice"), ("pacman", "libreoffice-fresh"), ("brew", "--cask libreoffice")),
        ["docker"] = Map(("winget", "Docker.DockerDesktop"), ("choco", "docker-desktop"), ("apt", "docker.io"), ("dnf", "docker"), ("pacman", "docker"), ("apk", "docker"), ("brew", "--cask docker")),
    };

    private static IReadOnlyDictionary<string, string> Map(params (string Manager, string Id)[] ids) => ids.ToDictionary(x => x.Manager, x => x.Id);

    /// <summary>Aliases the model may use.</summary>
    public static string Canonical(string name) => name.Trim().ToLowerInvariant() switch
    {
        "python3" or "py" or "pip" => "python",
        "nodejs" or "npm" or "node.js" => "node",
        "dotnet-sdk" or ".net" or ".net sdk" or "dotnet sdk" => "dotnet",
        "jdk" or "openjdk" => "java",
        "golang" => "go",
        "cargo" or "rustup" => "rust",
        var n => n,
    };

    /// <summary>Package managers found on this machine, best first.</summary>
    public static IReadOnlyList<string> Managers()
    {
        string[] order = OperatingSystem.IsWindows() ? ["winget", "scoop", "choco"]
            : OperatingSystem.IsMacOS() ? ["brew"]
            : ["apt", "dnf", "yum", "pacman", "apk", "zypper", "brew"];
        return order.Where(m => OnPath(m == "apt" ? "apt-get" : m)).ToList();
    }

    public static bool OnPath(string exe)
    {
        var exts = OperatingSystem.IsWindows() ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';') : [""];
        foreach (var dir in CurrentPath().Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
                if (File.Exists(Path.Combine(dir.Trim('"'), exe + ext.ToLowerInvariant())) || File.Exists(Path.Combine(dir.Trim('"'), exe + ext))) return true;
        return false;
    }

    /// <summary>
    /// PATH as it is now, including changes made by installers after this process started (Windows keeps them in the
    /// registry), plus well-known per-user tool folders.
    /// </summary>
    public static string CurrentPath()
    {
        var parts = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            parts.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "").Split(';'));
            parts.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "").Split(';'));
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            parts.Add(Path.Combine(local, "Microsoft", "dotnet"));
            parts.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
            parts.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            parts.Add(Path.Combine(home, ".dotnet"));
            parts.Add(Path.Combine(home, ".local", "bin"));
            parts.Add(Path.Combine(home, ".cargo", "bin"));
            parts.Add("/opt/homebrew/bin");
            parts.Add("/usr/local/bin");
        }
        parts.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        return string.Join(Path.PathSeparator, parts.Where(p => p.Length > 0).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
    }

    /// <summary>The shell commands that install <paramref name="tool"/> (pure; unit-tested).</summary>
    public static IReadOnlyList<string> Commands(string tool, string? version, string manager, bool windows)
    {
        // Language packages: "pip:openpyxl", "npm:pptxgenjs", "dotnet-tool:dotnet-ef" or manager = pip/npm/dotnet-tool.
        if (tool.IndexOf(':') is > 0 and var colon && tool[..colon] is "pip" or "npm" or "dotnet-tool")
            (manager, tool) = (tool[..colon], tool[(colon + 1)..]);
        var pinned = string.IsNullOrWhiteSpace(version) ? "" : version.Trim();
        switch (manager)
        {
            case "pip":
                return [$"{(windows ? "python" : "python3")} -m pip install --user --disable-pip-version-check {tool}{(pinned.Length > 0 ? "==" + pinned : "")}"];
            case "npm":
                return [$"npm install -g {tool}{(pinned.Length > 0 ? "@" + pinned : "")}"];
            case "dotnet-tool":
                return [$"dotnet tool update -g {tool}{(pinned.Length > 0 ? " --version " + pinned : "")}"];
        }
        tool = Canonical(tool);
        if (tool == "dotnet")
        {
            var channel = string.IsNullOrWhiteSpace(version) ? "LTS" : version.Trim();
            return windows
                ? [$"$ProgressPreference='SilentlyContinue'; Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\\dotnet-install.ps1 -UseBasicParsing; & $env:TEMP\\dotnet-install.ps1 -Channel {channel} -InstallDir \"$env:LOCALAPPDATA\\Microsoft\\dotnet\"; [Environment]::SetEnvironmentVariable('PATH', \"$env:LOCALAPPDATA\\Microsoft\\dotnet;\" + [Environment]::GetEnvironmentVariable('PATH','User'), 'User')"]
                : [$"curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && bash /tmp/dotnet-install.sh --channel {channel} --install-dir \"$HOME/.dotnet\" && (grep -q '.dotnet' ~/.profile || echo 'export PATH=\"$HOME/.dotnet:$PATH\"' >> ~/.profile)"];
        }
        var id = Catalog.TryGetValue(tool, out var ids) && ids.TryGetValue(manager, out var known) ? known : tool;
        var v = string.IsNullOrWhiteSpace(version) ? "" : version.Trim();
        return manager switch
        {
            "winget" =>
            [
                // Per-user first (no admin); fall back to the default scope.
                $"winget install --id {id} -e --scope user --silent --accept-package-agreements --accept-source-agreements --disable-interactivity{(v.Length > 0 ? " --version " + v : "")}; if ($LASTEXITCODE -ne 0) {{ winget install --id {id} -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity{(v.Length > 0 ? " --version " + v : "")} }}",
            ],
            "scoop" => [$"scoop install {id}"],
            "choco" => [$"choco install {id} -y --no-progress{(v.Length > 0 ? " --version " + v : "")}"],
            "apt" => [$"{Sudo()}apt-get update -qq && {Sudo()}DEBIAN_FRONTEND=noninteractive apt-get install -y -qq {id}"],
            "dnf" => [$"{Sudo()}dnf install -y {id}"],
            "yum" => [$"{Sudo()}yum install -y {id}"],
            "pacman" => [$"{Sudo()}pacman -S --noconfirm --needed {id}"],
            "apk" => [$"{Sudo()}apk add --no-cache {id}"],
            "zypper" => [$"{Sudo()}zypper --non-interactive install {id}"],
            "brew" => [$"brew install {id}"],
            _ => [],
        };
    }

    private static string Sudo() => !OperatingSystem.IsWindows() && Environment.UserName != "root" ? "sudo -n " : "";

    /// <summary>Command that prints the installed version, used to check before and after installing.</summary>
    public static string VersionCommand(string tool, string? manager = null)
    {
        if (tool.IndexOf(':') is > 0 and var colon && tool[..colon] is "pip" or "npm" or "dotnet-tool")
            (manager, tool) = (tool[..colon], tool[(colon + 1)..]);
        return manager switch
        {
            "pip" => $"{(OperatingSystem.IsWindows() ? "python" : "python3")} -m pip show {tool}",
            "npm" => $"npm ls -g {tool} --depth=0",
            "dotnet-tool" => $"dotnet tool list -g | {(OperatingSystem.IsWindows() ? "Select-String" : "grep -i")} {tool}",
            _ => SystemVersionCommand(tool),
        };
    }

    private static string SystemVersionCommand(string tool) => Canonical(tool) switch
    {
        "python" => OperatingSystem.IsWindows() ? "python --version" : "python3 --version",
        "node" => "node --version",
        "dotnet" => "dotnet --list-sdks",
        "git" => "git --version",
        "java" => "java -version",
        "go" => "go version",
        "rust" => "cargo --version",
        "docker" => "docker --version",
        "ffmpeg" => "ffmpeg -version",
        "pandoc" => "pandoc --version",
        var t => $"{t} --version",
    };
}

public sealed class InstallPackageFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "install_package",
        "Install a prerequisite on the computer your tools run on: tools (python, dotnet, node, git, java, go, rust, ffmpeg, pandoc, libreoffice, docker, or any id of winget/choco/scoop, apt/dnf/yum/pacman/apk/zypper, brew) and language packages with a prefix: pip:openpyxl, npm:pptxgenjs, dotnet-tool:dotnet-ef. Checks first and skips what is already installed. After it succeeds, run_shell sees the new tool.",
        Schema(("name", "string", "Tool or package, e.g. python, dotnet, node, git, or a manager-specific id", true),
               ("version", "string", "Optional version or channel (e.g. 10.0 for dotnet)", false),
               ("manager", "string", "Optional package manager to use (default: the best one found)", false)),
        "shell", PermissionCategory.ProcessExecution, RiskLevel.High, 1200);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        var name = call.Require("name");
        var version = call.GetString("version");
        var windows = OperatingSystem.IsWindows();
        var managers = Prerequisites.Managers();
        var requested = call.GetString("manager");
        var language = requested is "pip" or "npm" or "dotnet-tool" || name.IndexOf(':') is > 0 and var c && name[..c] is "pip" or "npm" or "dotnet-tool";
        var manager = requested is { Length: > 0 } m ? m : language ? name[..name.IndexOf(':')] : managers.FirstOrDefault();
        var log = new StringBuilder();

        var (before, beforeOut) = await RunAsync(Prerequisites.VersionCommand(name, manager), windows, ctx.WorkspacePath, TimeSpan.FromSeconds(30), ct);
        if (before == 0 && string.IsNullOrWhiteSpace(version))
            return FunctionResult.Ok($"{name} is already installed: {beforeOut.Trim()}");

        if (manager is null && !language && Prerequisites.Canonical(name) != "dotnet")
            return FunctionResult.Fail($"No package manager found on {RuntimeInformation.OSDescription}. Install winget/apt/brew, or download the installer with run_shell.");
        foreach (var command in Prerequisites.Commands(name, version, manager ?? "", windows))
        {
            log.AppendLine("$ " + command);
            var (exit, output) = await RunAsync(command, windows, ctx.WorkspacePath, TimeSpan.FromMinutes(18), ct);
            log.AppendLine(Truncate(output.Trim(), 6000)).AppendLine($"exit code: {exit}");
        }
        var (after, afterOut) = await RunAsync(Prerequisites.VersionCommand(name, manager), windows, ctx.WorkspacePath, TimeSpan.FromSeconds(30), ct);
        return after == 0
            ? FunctionResult.Ok($"Installed {name} with {manager ?? "dotnet-install"}: {afterOut.Trim()}\n\n{log}")
            : FunctionResult.Fail($"{name} is still not available after installing with {manager}. It may need administrator rights or a new login.\n\n{log}\nCheck output: {afterOut.Trim()}");
    }

    private static async Task<(int Exit, string Output)> RunAsync(string command, bool windows, string cwd, TimeSpan timeout, CancellationToken ct)
    {
        Directory.CreateDirectory(cwd);
        var psi = windows
            ? new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command } }
            : new ProcessStartInfo("/bin/bash") { ArgumentList = { "-lc", command } };
        psi.WorkingDirectory = cwd;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.Environment["PATH"] = Prerequisites.CurrentPath();
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            return (-1, "timed out");
        }
        return (p.ExitCode, (await stdout) + (await stderr));
    }
}
