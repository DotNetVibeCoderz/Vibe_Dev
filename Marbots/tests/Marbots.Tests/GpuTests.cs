using Marbots.Abstractions;
using Marbots.Kernel;
using Marbots.Runtime;

namespace Marbots.Tests;

public class GpuTests
{
    [Fact]
    public void Nvidia_smi_csv_is_parsed()
    {
        var gpus = GpuDetector.ParseNvidiaSmi("NVIDIA GeForce RTX 4090, 24564, 1200, 7\nNVIDIA A100-SXM4-80GB, 81920, 0, 0\n");
        Assert.Equal(2, gpus.Count);
        Assert.Equal(("NVIDIA GeForce RTX 4090", 24564L, 1200L, 7.0), (gpus[0].Name, gpus[0].MemoryMb, gpus[0].UsedMemoryMb!.Value, gpus[0].UtilizationPercent!.Value));
        Assert.All(gpus, g => Assert.Equal("cuda", g.Api));
        Assert.Equal(["gpu", "cuda"], GpuDetector.Capabilities(gpus));
        Assert.Empty(GpuDetector.ParseNvidiaSmi(null));
    }

    [Fact]
    public void Apple_silicon_reports_metal_with_unified_memory()
    {
        const string json = """{"SPDisplaysDataType":[{"_name":"Apple M2 Pro","sppci_model":"Apple M2 Pro","spdisplays_vendor":"sppci_vendor_Apple","spdisplays_mtlgpufamilysupport":"spdisplays_metal3","sppci_cores":"19"}]}""";
        var gpu = Assert.Single(GpuDetector.ParseSystemProfiler(json, 32768));
        Assert.Equal(("Apple M2 Pro", "Apple", "metal", 24576L), (gpu.Name, gpu.Vendor, gpu.Api, gpu.MemoryMb));
        const string intelMac = """{"SPDisplaysDataType":[{"sppci_model":"AMD Radeon Pro 5500M","spdisplays_vendor":"sppci_vendor_amd","spdisplays_vram":"8 GB","spdisplays_metalfamily":"spdisplays_mtlgpufamilymac2"}]}""";
        var radeon = Assert.Single(GpuDetector.ParseSystemProfiler(intelMac, 16384));
        Assert.Equal((8192L, "AMD"), (radeon.MemoryMb, radeon.Vendor));
        const string igpu = """{"SPDisplaysDataType":[{"sppci_model":"Intel UHD Graphics 630","spdisplays_vendor":"Intel","spdisplays_vram_shared":"1536 MB","spdisplays_mtlgpufamilysupport":"spdisplays_metal3"}]}""";
        Assert.Equal(1536, Assert.Single(GpuDetector.ParseSystemProfiler(igpu, 32768)).MemoryMb);
    }

    [Fact]
    public void Windows_wmi_and_rocm_output_are_parsed()
    {
        var wmi = GpuDetector.ParseWmi("Microsoft Basic Display Adapter|0\nAMD Radeon RX 7900 XTX|4293918720\nIntel(R) UHD Graphics 770|1073741824\n");
        Assert.Equal(["AMD", "Intel"], wmi.Select(g => g.Vendor));
        Assert.Equal(4095, wmi[0].MemoryMb);
        var rocm = GpuDetector.ParseRocmSmi("device,Card series,Card model,VRAM Total Memory (B),VRAM Total Used Memory (B)\ncard0,AMD Instinct MI300X,0x74a1,206141652992,1073741824\n");
        var mi = Assert.Single(rocm);
        Assert.Equal(("AMD Instinct MI300X", "rocm", 196592L, 1024L), (mi.Name, mi.Api, mi.MemoryMb, mi.UsedMemoryMb!.Value));
    }

    private static PlacementService.HostCandidate H(string id, GpuInfo[] gpus, double? gpuPct = null, long? gpuFree = null, double cpu = 10) =>
        new(id, ["shell", .. GpuDetector.Capabilities(gpus)], new HostMetrics { CpuPercent = cpu, FreeMemoryMb = 8000, GpuPercent = gpuPct, FreeGpuMemoryMb = gpuFree }, false, gpus);

    [Fact]
    public void Gpu_bots_go_to_hosts_with_enough_free_gpu_memory()
    {
        var small = new GpuInfo { Name = "RTX 3060", Vendor = "NVIDIA", Api = "cuda", MemoryMb = 12288 };
        var big = new GpuInfo { Name = "RTX 4090", Vendor = "NVIDIA", Api = "cuda", MemoryMb = 24576 };
        var mac = new GpuInfo { Name = "Apple M2 Max", Vendor = "Apple", Api = "metal", MemoryMb = 49152 };
        var hosts = new[]
        {
            new PlacementService.HostCandidate("local-default", ["shell"], null, true),
            H("pc-small", [small], gpuPct: 0, gpuFree: 12000),
            H("pc-big-busy", [big], gpuPct: 95, gpuFree: 2000),
            H("pc-big-idle", [big], gpuPct: 2, gpuFree: 24000, cpu: 40),
            H("mac", [mac]),
        };
        var bot = new BotDefinition { Requires = ["CUDA", "gpu:16"] };
        Assert.Equal(["cuda", "gpu:16"], PlacementService.Required(bot));
        Assert.Equal("pc-big-idle", PlacementService.Choose(PlacementService.Required(bot), hosts, needsContainer: false));
        Assert.Equal("mac", PlacementService.Choose(["metal"], hosts, needsContainer: false));
        Assert.Equal("mac", PlacementService.Choose(["gpu:32"], hosts, needsContainer: false));
        Assert.Equal("local-default", PlacementService.Choose(["gpu:128"], hosts, needsContainer: false));
        Assert.False(PlacementService.Meets(hosts[1], "gpu:16"));
        Assert.True(PlacementService.Meets(hosts[1], "gpu:12"));
    }
}
