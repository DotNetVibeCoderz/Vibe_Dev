using Marbots.Abstractions;
using Marbots.Runtime;

namespace Marbots.Tests;

public class ProvisionTests
{
    [Fact]
    public void Docker_run_has_quotas_gpu_identity_volume_and_enroll_once()
    {
        var req = new ProvisionHostRequest("GPU Sandbox", "http://host.docker.internal:5170", Cpus: 4, MemoryMb: 8192, Gpu: true, Network: false);
        var args = HostProvisioner.DockerRunArgs("marbots-host-gpu-sandbox", req, "/srv/bin", "http://host.docker.internal:5170/t/acme", "mbe_TOKEN");
        var line = string.Join(' ', args);
        Assert.StartsWith("run -d --name marbots-host-gpu-sandbox --restart unless-stopped --cpus 4 --memory 8192m", line);
        Assert.Contains("--gpus all", line);
        Assert.Contains("--network none", line);
        Assert.Contains("-v marbots-host-gpu-sandbox:/var/lib/marbots", line);
        Assert.Contains("-v /srv/bin:/opt/marbots:ro", line);
        Assert.Contains(HostProvisioner.DefaultImage, args);
        var script = args[^1];
        Assert.Equal("/bin/sh", args[^3]);
        Assert.StartsWith("test -f /var/lib/marbots/host.json || /opt/marbots/marbots-host enroll --server http://host.docker.internal:5170/t/acme --token mbe_TOKEN --name gpu-sandbox;", script);
        Assert.EndsWith("exec /opt/marbots/marbots-host run", script);
    }

    [Fact]
    public void Remote_commands_quote_for_bash_and_powershell()
    {
        var args = HostProvisioner.DockerRunArgs("marbots-host-a", new ProvisionHostRequest("a", "http://x:5170"), "$(pwd)", "http://x:5170", "mbe_T");
        var bash = HostProvisioner.RemoteCommand(args, powershell: false);
        Assert.StartsWith("chmod +x marbots-host && docker run -d", bash);
        Assert.Contains("-v \"$(pwd):/opt/marbots:ro\"", bash);
        Assert.Contains("'test -f /var/lib/marbots/host.json || ", bash);
        var ps = HostProvisioner.RemoteCommand(HostProvisioner.DockerRunArgs("marbots-host-a", new ProvisionHostRequest("a", "http://x:5170"), "${PWD}", "http://x:5170", "mbe_T"), powershell: true);
        Assert.StartsWith("docker run -d", ps);
        Assert.Contains("-v \"${PWD}:/opt/marbots:ro\"", ps);
        Assert.DoesNotContain("chmod", ps);
    }
}
