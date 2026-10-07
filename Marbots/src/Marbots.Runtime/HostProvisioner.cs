using System.Diagnostics;
using Marbots.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

/// <summary>
/// Provisions disposable "VM-like" hosts: a container running marbots-host on a Docker-capable machine (this server or
/// any connected host with the docker capability). The container enrolls itself with a one-time token, keeps its
/// identity in a named volume, restarts with Docker, and is removed again when the host is removed in Marbots.
/// </summary>
public sealed class HostProvisioner(HostRegistry registry, HostConnectionManager connections, HostBootstrapper bootstrapper, MarbotsOptions options, ILogger<HostProvisioner> log)
{
    public const string DefaultImage = "mcr.microsoft.com/dotnet/runtime-deps:10.0";

    public async Task<ProvisionHostResult> ProvisionAsync(ProvisionHostRequest req, CancellationToken ct)
    {
        var steps = new List<string>();
        var name = Ids.Slug(req.Name);
        if (name.Length == 0) return Fail("A name is required (letters, digits, '-').");
        if (!Uri.TryCreate(req.ServerUrl, UriKind.Absolute, out var server)) return Fail("ServerUrl must be the address the container uses to reach this server, e.g. http://host.docker.internal:5170.");
        var onHost = string.IsNullOrWhiteSpace(req.OnHost) ? WellKnown.LocalHostId : req.OnHost;
        var remote = onHost != WellKnown.LocalHostId;
        if (remote && !connections.IsOnline(onHost)) return Fail($"Host {onHost} is offline.");
        if (remote && connections.HelloOf(onHost) is { } hello && !hello.Capabilities.Contains("docker")) return Fail($"Host {onHost} has no running Docker.");
        var arch = req.Arch is "arm64" ? "arm64" : "x64";
        var package = bootstrapper.PackageFor("linux-" + arch);
        if (package is null) return Fail($"No marbots-host package for linux-{arch} in {bootstrapper.PackagesDirectory}.");

        var (token, _) = await registry.CreateEnrollmentAsync(req.Name, TimeSpan.FromMinutes(30), "provision", ct);
        var container = "marbots-host-" + name;
        var serverUrl = Tenants.ServerUrlFor(server.AbsoluteUri, options.TenantId);
        steps.Add($"One-time enrollment token created for {req.Name}.");
        var known = (await registry.ListAsync(ct)).Select(h => h.Id).ToHashSet();
        try
        {
            string output;
            if (!remote)
            {
                var dir = Path.Combine(bootstrapper.PackagesDirectory, "provision", arch);
                Directory.CreateDirectory(dir);
                File.Copy(package, Path.Combine(dir, "marbots-host"), overwrite: true);
                var args = DockerRunArgs(container, req, dir, serverUrl, token);
                steps.Add("docker " + Redact(string.Join(' ', args), token));
                output = await RunLocalDockerAsync(args, ct);
            }
            else
            {
                var workspace = "provision-" + arch;
                await connections.PutFilesAsync(onHost, workspace, [new HostFilePayload("marbots-host", Convert.ToBase64String(await File.ReadAllBytesAsync(package, ct)))], ct);
                steps.Add($"Uploaded marbots-host (linux-{arch}) to {onHost}.");
                var windows = connections.HelloOf(onHost)?.Os.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true;
                var command = RemoteCommand(DockerRunArgs(container, req, windows ? "${PWD}" : "$(pwd)", serverUrl, token), windows);
                steps.Add(Redact(command, token));
                var r = await connections.InvokeAsync(onHost, new HostInvoke
                {
                    Function = "run_shell", CallId = Guid.NewGuid().ToString("N")[..8], TaskId = "provision", Workspace = workspace,
                    Arguments = new System.Text.Json.Nodes.JsonObject { ["command"] = command, ["timeout_seconds"] = 600 }.ToJsonString(),
                }, TimeSpan.FromMinutes(6), ct);
                if (!r.Success) return Fail(r.Content, steps);
                output = r.Content;
            }
            steps.Add("Container started: " + output.Trim().Split('\n').LastOrDefault()?.Trim());

            // Wait for the container to enroll and connect.
            var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(req.WaitSeconds, 5, 600));
            while (DateTime.UtcNow < deadline)
            {
                var added = (await registry.ListAsync(ct)).FirstOrDefault(h => !known.Contains(h.Id) && h.Name == req.Name);
                if (added is not null)
                {
                    added.Kind = "container";
                    added.ProvisionedOn = onHost;
                    added.ContainerName = container;
                    await registry.SaveAsync(added, ct);
                    steps.Add($"Enrolled as {added.Id}.");
                    return new ProvisionHostResult(true, added.Id, container, steps, null);
                }
                await Task.Delay(1000, ct);
            }
            return Fail("The container started but did not enroll in time; check `docker logs " + container + "` and that it can reach " + serverUrl + ".", steps);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or HostOfflineException or TimeoutException)
        {
            log.LogWarning(ex, "Provisioning {Name} failed", req.Name);
            return Fail(ex.Message, steps);
        }

        ProvisionHostResult Fail(string error, List<string>? s = null) => new(false, null, null, s ?? steps, error);
    }

    /// <summary>Stops and removes a provisioned host's container (and its identity volume).</summary>
    public async Task<string?> DeprovisionAsync(HostRecord host, CancellationToken ct)
    {
        if (host.Kind != "container" || host.ContainerName is not { Length: > 0 } name) return null;
        string[] rm = ["rm", "-f", name];
        string[] vol = ["volume", "rm", "-f", name];
        try
        {
            if (host.ProvisionedOn is null or WellKnown.LocalHostId)
            {
                await RunLocalDockerAsync(rm, ct);
                await RunLocalDockerAsync(vol, ct);
                return null;
            }
            if (!connections.IsOnline(host.ProvisionedOn)) return $"{host.ProvisionedOn} is offline; remove container {name} there by hand.";
            var command = $"docker rm -f {name}; docker volume rm -f {name}";
            var r = await connections.InvokeAsync(host.ProvisionedOn, new HostInvoke
            {
                Function = "run_shell", CallId = Guid.NewGuid().ToString("N")[..8], TaskId = "deprovision", Workspace = "provision-x64",
                Arguments = new System.Text.Json.Nodes.JsonObject { ["command"] = command }.ToJsonString(),
            }, TimeSpan.FromMinutes(2), ct);
            return r.Success ? null : r.Content;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or HostOfflineException or TimeoutException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// docker run arguments: quotas, GPU, a named volume for the host identity, the host binary mounted read-only, and a
    /// command that enrolls once (first start) and then runs. Pure, so it can be tested.
    /// </summary>
    public static List<string> DockerRunArgs(string container, ProvisionHostRequest req, string binaryDir, string serverUrl, string token)
    {
        var args = new List<string>
        {
            "run", "-d", "--name", container, "--restart", "unless-stopped",
            "--cpus", req.Cpus.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), "--memory", $"{Math.Max(256, req.MemoryMb)}m",
            "--add-host", "host.docker.internal:host-gateway",
            "-e", "MARBOTS_HOST_HOME=/var/lib/marbots", "-e", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1",
            "-v", $"{container}:/var/lib/marbots", "-v", $"{binaryDir}:/opt/marbots:ro",
            "--label", "marbots.host=" + req.Name,
        };
        if (req.Gpu) args.AddRange(["--gpus", "all"]);
        if (!req.Network) args.AddRange(["--network", "none"]);
        var image = string.IsNullOrWhiteSpace(req.Image) ? DefaultImage : req.Image;
        var run = $"test -f /var/lib/marbots/host.json || /opt/marbots/marbots-host enroll --server {serverUrl} --token {token} --name {Ids.Slug(req.Name)}; exec /opt/marbots/marbots-host run";
        args.AddRange([image, "/bin/sh", "-c", run]);
        return args;
    }

    /// <summary>The docker command line for the remote shell (bash or PowerShell quoting).</summary>
    public static string RemoteCommand(IReadOnlyList<string> args, bool powershell)
    {
        string Q(string a) => a.Length > 0 && a.All(c => char.IsLetterOrDigit(c) || "-_.:/=,@".Contains(c)) ? a
            : powershell ? (a.StartsWith("${PWD}", StringComparison.Ordinal) ? "\"" + a + "\"" : "'" + a.Replace("'", "''") + "'")
            : a.StartsWith("$(pwd)", StringComparison.Ordinal) ? "\"" + a + "\"" : "'" + a.Replace("'", "'\\''") + "'";
        return (powershell ? "" : "chmod +x marbots-host && ") + "docker " + string.Join(' ', args.Select(Q));
    }

    private static string Redact(string s, string token) => s.Replace(token, "mbe_***", StringComparison.Ordinal);

    private static async Task<string> RunLocalDockerAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start docker.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"docker {psi.ArgumentList.FirstOrDefault()} failed: {(await stderr).Trim()}");
        return await stdout;
    }
}
