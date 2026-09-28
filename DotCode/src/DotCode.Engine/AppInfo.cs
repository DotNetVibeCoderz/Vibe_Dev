using System.Reflection;

namespace DotCode.Engine;

public static class AppInfo
{
    public const string Name = "DotCode";
    public const string Company = "Gravicode Studios";
    public const string Lead = "Kang Fadhil";
    public const string Credit = "Built by Gravicode Studios, led by Kang Fadhil";
    public const string CreditId = "Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil";
    public const string Repository = "https://github.com/DotNetVibeCoderz/Vibe_Dev";
    public const string RepositorySlug = "DotNetVibeCoderz/Vibe_Dev";
    /// <summary>Git tag prefix of DotCode releases (the repository hosts other projects too).</summary>
    public const string ReleaseTagPrefix = "dotcode-v";

    /// <summary>Product version, stamped at build time (<c>-p:Version=…</c>; release builds use the tag).</summary>
    public static readonly string Version = ReadVersion();

    private static string ReadVersion()
    {
        var info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        // Drop SourceLink's "+<commit sha>" build metadata.
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }
}
