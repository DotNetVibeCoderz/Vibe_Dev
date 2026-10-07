using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Marbots.Abstractions;

namespace Marbots.Kernel;

/// <summary>
/// Finds GPUs: nvidia-smi (NVIDIA, Windows/Linux), rocm-smi (AMD, Linux), system_profiler (Apple, unified memory) and
/// WMI (any Windows adapter). Each probe has a short timeout; a machine without GPUs simply reports none.
/// </summary>
public static class GpuDetector
{
    public static List<GpuInfo> Detect()
    {
        var nvidia = ParseNvidiaSmi(Run("nvidia-smi", "--query-gpu=name,memory.total,memory.used,utilization.gpu --format=csv,noheader,nounits"));
        if (nvidia.Count > 0) return nvidia;
        if (OperatingSystem.IsMacOS())
            return ParseSystemProfiler(Run("system_profiler", "SPDisplaysDataType -json"), GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
        if (OperatingSystem.IsLinux() && ParseRocmSmi(Run("rocm-smi", "--showproductname --showmeminfo vram --csv")) is { Count: > 0 } amd) return amd;
        if (OperatingSystem.IsWindows())
            return ParseWmi(Run("powershell", "-NoProfile -Command \"Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name + '|' + $_.AdapterRAM }\""));
        return [];
    }

    /// <summary>Live utilisation for the heartbeat (NVIDIA only; other vendors report none).</summary>
    public static (double? Percent, long? FreeMb) Sample()
    {
        var gpus = ParseNvidiaSmi(Run("nvidia-smi", "--query-gpu=name,memory.total,memory.used,utilization.gpu --format=csv,noheader,nounits"));
        if (gpus.Count == 0) return (null, null);
        return (gpus.Average(g => g.UtilizationPercent ?? 0), gpus.Sum(g => g.MemoryMb - (g.UsedMemoryMb ?? 0)));
    }

    /// <summary>Capabilities a host advertises for its GPUs: "gpu" plus the compute API (cuda, rocm, metal, directx).</summary>
    public static IEnumerable<string> Capabilities(IReadOnlyCollection<GpuInfo> gpus) =>
        gpus.Count == 0 ? [] : new[] { "gpu" }.Concat(gpus.Select(g => g.Api)).Where(a => a.Length > 0).Distinct();

    public static List<GpuInfo> ParseNvidiaSmi(string? csv)
    {
        var list = new List<GpuInfo>();
        foreach (var line in (csv ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split(',', StringSplitOptions.TrimEntries);
            if (p.Length < 2 || !long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)) continue;
            list.Add(new GpuInfo
            {
                Name = p[0], Vendor = "NVIDIA", Api = "cuda", MemoryMb = total,
                UsedMemoryMb = p.Length > 2 && long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var used) ? used : null,
                UtilizationPercent = p.Length > 3 && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var u) ? u : null,
            });
        }
        return list;
    }

    /// <summary>Apple GPUs share system memory; Metal lets a process use roughly three quarters of it.</summary>
    public static List<GpuInfo> ParseSystemProfiler(string? json, long totalMemoryMb)
    {
        var list = new List<GpuInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("SPDisplaysDataType", out var arr)) return list;
            foreach (var g in arr.EnumerateArray())
            {
                var name = g.TryGetProperty("sppci_model", out var m) ? m.GetString() ?? "GPU" : g.TryGetProperty("_name", out var n) ? n.GetString() ?? "GPU" : "GPU";
                var vendor = Vendor(g.TryGetProperty("spdisplays_vendor", out var v) ? (v.GetString() ?? "").Replace("sppci_vendor_", "", StringComparison.Ordinal) : "Apple");
                // Dedicated VRAM, else the shared (dynamic) maximum of an Intel iGPU, else Apple silicon's unified memory.
                long memory = totalMemoryMb * 3 / 4;
                if (g.TryGetProperty("spdisplays_vram", out var vram) && ParseSize(vram.GetString()) is { } dedicated) memory = dedicated;
                else if (g.TryGetProperty("spdisplays_vram_shared", out var shared) && ParseSize(shared.GetString()) is { } dynamic) memory = dynamic;
                var metal = g.TryGetProperty("spdisplays_mtlgpufamilysupport", out _) || g.TryGetProperty("spdisplays_metal", out _) || g.TryGetProperty("spdisplays_metalfamily", out _);
                list.Add(new GpuInfo { Name = name, Vendor = vendor.Length == 0 ? "Apple" : vendor, Api = metal ? "metal" : "", MemoryMb = memory });
            }
        }
        catch (JsonException) { }
        return list;
    }

    public static List<GpuInfo> ParseRocmSmi(string? csv)
    {
        var list = new List<GpuInfo>();
        var lines = (csv ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return list;
        var header = lines[0].Split(',').Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string part) => header.FindIndex(h => h.Contains(part, StringComparison.Ordinal));
        var (nameCol, totalCol, usedCol) = (Col("card series"), Col("total memory"), Col("total used memory"));
        foreach (var line in lines.Skip(1))
        {
            var p = line.Split(',');
            long? Bytes(int i) => i >= 0 && i < p.Length && long.TryParse(p[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : null;
            if (Bytes(totalCol) is not { } total) continue;
            list.Add(new GpuInfo
            {
                Name = nameCol >= 0 && nameCol < p.Length ? p[nameCol].Trim() : "AMD GPU", Vendor = "AMD", Api = "rocm",
                MemoryMb = total / 1024 / 1024, UsedMemoryMb = Bytes(usedCol) / 1024 / 1024,
            });
        }
        return list;
    }

    /// <summary>"Name|AdapterRAM" lines. AdapterRAM is a 32-bit field, so cards above 4 GB report 4 GB at most.</summary>
    public static List<GpuInfo> ParseWmi(string? lines)
    {
        var list = new List<GpuInfo>();
        foreach (var line in (lines ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            var name = p[0].Trim();
            if (name.Length == 0 || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;
            var vendor = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD"
                : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "";
            var bytes = p.Length > 1 && long.TryParse(p[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0;
            list.Add(new GpuInfo { Name = name, Vendor = vendor, Api = "directx", MemoryMb = bytes / 1024 / 1024 });
        }
        return list;
    }

    private static string Vendor(string v) => v.ToLowerInvariant() switch
    {
        "" or "apple" => "Apple",
        "amd" or "ati" => "AMD",
        "intel" => "Intel",
        "nvidia" => "NVIDIA",
        _ => v,
    };

    private static long? ParseSize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        var unit = parts.Length > 1 ? parts[1].ToUpperInvariant() : "MB";
        return (long)(unit.StartsWith('G') ? value * 1024 : value);
    }

    private static string? Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(8000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
