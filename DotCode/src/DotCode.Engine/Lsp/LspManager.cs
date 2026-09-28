using System.Collections.Concurrent;
using DotCode.Engine.Configuration;
using DotCode.Engine.Util;

namespace DotCode.Engine.Lsp;

/// <summary>A language server DotCode can use, resolved from built-in defaults and the <c>lsp.servers</c> settings.</summary>
public sealed record LspServerDef(string Name, string Command, IReadOnlyList<string> Args, IReadOnlyList<string> Extensions,
    IReadOnlyList<string> RootMarkers, string? LanguageId, System.Text.Json.JsonElement? InitializationOptions,
    IReadOnlyDictionary<string, string>? Env, bool Available);

/// <summary>Finds the language server for a file and starts it on first use (one instance per server and workspace
/// root). Built-in defaults are used when their executable is on PATH or in the project's node_modules/.bin.</summary>
public sealed class LspManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<LspClient>>> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _projectRoot;

    public IReadOnlyList<LspServerDef> Servers { get; }
    public bool Enabled { get; }
    public bool DiagnosticsAfterEdit { get; }

    private static readonly (string Name, string Command, string[] Args, string[] Extensions, string[] Markers)[] Builtins =
    [
        ("typescript", "typescript-language-server", ["--stdio"], [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts"], ["tsconfig.json", "jsconfig.json", "package.json"]),
        ("python", "pyright-langserver", ["--stdio"], [".py", ".pyi"], ["pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", "pyrightconfig.json"]),
        ("go", "gopls", [], [".go"], ["go.mod", "go.work"]),
        ("rust", "rust-analyzer", [], [".rs"], ["Cargo.toml"]),
        ("csharp", "csharp-ls", [], [".cs"], ["*.sln", "*.slnx", "*.csproj"]),
        ("cpp", "clangd", [], [".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh"], ["compile_commands.json", "CMakeLists.txt", ".clangd"]),
        ("java", "jdtls", [], [".java"], ["pom.xml", "build.gradle", "build.gradle.kts"]),
    ];

    public LspManager(Settings settings, string projectRoot)
    {
        _projectRoot = projectRoot;
        var lsp = settings.Lsp;
        Enabled = lsp?.Enabled != false;
        DiagnosticsAfterEdit = lsp?.DiagnosticsAfterEdit != false;
        var configured = lsp?.Servers ?? [];
        var list = new List<LspServerDef>();
        foreach (var (name, command, args, exts, markers) in Builtins)
        {
            configured.TryGetValue(name, out var o);
            if (o?.Disabled == true) continue;
            list.Add(Resolve(name, o?.Command ?? command, o?.Args ?? [.. args], o?.Extensions ?? [.. exts], o?.RootMarkers ?? [.. markers], o));
        }
        foreach (var (name, o) in configured)
        {
            if (Builtins.Any(b => b.Name == name) || o.Disabled == true || o.Command is null || o.Extensions is not { Count: > 0 }) continue;
            list.Add(Resolve(name, o.Command, o.Args ?? [], o.Extensions, o.RootMarkers ?? [], o));
        }
        Servers = list;
    }

    private LspServerDef Resolve(string name, string command, List<string> args, List<string> extensions, List<string> markers, LspServerConfig? o)
    {
        var expanded = Providers.ConfigValue.Expand(command) ?? command;
        var exe = Path.IsPathRooted(expanded) ? (File.Exists(expanded) ? expanded : null) : FindExecutable(expanded);
        return new LspServerDef(name, exe ?? expanded, args, [.. extensions.Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())],
            markers, o?.LanguageId, o?.InitializationOptions, o?.Env, exe is not null);
    }

    /// <summary>PATH, then node_modules/.bin of the project (locally installed servers such as typescript-language-server).</summary>
    private string? FindExecutable(string command)
    {
        if (ProcessRunner.FindOnPath(command) is { } onPath) return onPath;
        var bin = Path.Combine(_projectRoot, "node_modules", ".bin");
        foreach (var candidate in OperatingSystem.IsWindows() ? [command + ".cmd", command + ".exe", command] : new[] { command })
        {
            var p = Path.Combine(bin, candidate);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public bool AnyAvailable => Enabled && Servers.Any(s => s.Available);

    public LspServerDef? ServerFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return Servers.FirstOrDefault(s => s.Available && s.Extensions.Contains(ext))
               ?? Servers.FirstOrDefault(s => s.Extensions.Contains(ext));
    }

    public static string LanguageId(string path, LspServerDef def) => def.LanguageId ?? Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ts" or ".mts" or ".cts" => "typescript",
        ".tsx" => "typescriptreact",
        ".js" or ".mjs" or ".cjs" => "javascript",
        ".jsx" => "javascriptreact",
        ".py" or ".pyi" => "python",
        ".go" => "go",
        ".rs" => "rust",
        ".cs" => "csharp",
        ".c" or ".h" => "c",
        ".cc" or ".cpp" or ".cxx" or ".hpp" or ".hh" => "cpp",
        ".java" => "java",
        var e => e.TrimStart('.'),
    };

    /// <summary>Nearest ancestor of the file (up to the project root) containing a root marker; else the project root.</summary>
    public string RootFor(string path, LspServerDef def)
    {
        var projectRoot = Path.GetFullPath(_projectRoot);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        while (dir is not null && DotCodePaths.IsUnder(dir, projectRoot))
        {
            foreach (var marker in def.RootMarkers)
            {
                try
                {
                    if (marker.Contains('*') ? Directory.EnumerateFiles(dir, marker).Any() : File.Exists(Path.Combine(dir, marker)) || Directory.Exists(Path.Combine(dir, marker)))
                        return dir;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (string.Equals(dir.TrimEnd('/', '\\'), projectRoot.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)) break;
            dir = Path.GetDirectoryName(dir);
        }
        return DotCodePaths.IsUnder(path, projectRoot) ? projectRoot : Path.GetDirectoryName(Path.GetFullPath(path))!;
    }

    /// <summary>The running (or starting) client for a file; starts the server on first use.</summary>
    public async Task<(LspClient Client, LspServerDef Def)> GetClientAsync(string path, CancellationToken ct)
    {
        var def = ServerFor(path) ?? throw new LspException($"No language server is configured for {Path.GetExtension(path)} files. Add one under \"lsp.servers\" in settings.");
        if (!def.Available)
            throw new LspException($"The {def.Name} language server ('{def.Command}') is not installed or not on PATH. " + InstallHint(def.Name));
        var root = RootFor(path, def);
        var key = def.Name + "|" + root;
        var lazy = _clients.GetOrAdd(key, _ => new Lazy<Task<LspClient>>(() =>
            LspClient.StartAsync(def.Name, def.Command, def.Args, root, def.InitializationOptions, def.Env, CancellationToken.None)));
        try
        {
            var client = await lazy.Value.WaitAsync(TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
            if (client.IsAlive) return (client, def);
        }
        catch (Exception ex) when (ex is LspException or InvalidOperationException or TimeoutException)
        {
            _clients.TryRemove(key, out _);
            throw new LspException(ex.Message);
        }
        // The server died: start a fresh one next time.
        _clients.TryRemove(key, out _);
        throw new LspException($"The {def.Name} language server stopped unexpectedly; it will be restarted on the next request.");
    }

    /// <summary>A client that is already running for this file (never starts a server).</summary>
    public LspClient? RunningClientFor(string path)
    {
        if (ServerFor(path) is not { Available: true } def) return null;
        var key = def.Name + "|" + RootFor(path, def);
        return _clients.TryGetValue(key, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully && lazy.Value.Result.IsAlive
            ? lazy.Value.Result
            : null;
    }

    public IEnumerable<LspClient> RunningClients =>
        _clients.Values.Where(l => l.IsValueCreated && l.Value.IsCompletedSuccessfully).Select(l => l.Value.Result).Where(c => c.IsAlive);

    public static string InstallHint(string name) => name switch
    {
        "typescript" => "Install: npm i -g typescript typescript-language-server",
        "python" => "Install: npm i -g pyright (or pip install pyright)",
        "go" => "Install: go install golang.org/x/tools/gopls@latest",
        "rust" => "Install: rustup component add rust-analyzer",
        "csharp" => "Install: dotnet tool install -g csharp-ls",
        "cpp" => "Install clangd from LLVM (https://clangd.llvm.org/installation)",
        "java" => "Install Eclipse JDT Language Server (jdtls)",
        _ => "",
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _clients.Values)
        {
            if (!lazy.IsValueCreated) continue;
            try { await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { }
        }
        _clients.Clear();
    }
}
