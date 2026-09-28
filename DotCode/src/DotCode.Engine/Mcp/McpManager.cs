using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Configuration;
using DotCode.Engine.Tools;

namespace DotCode.Engine.Mcp;

public enum McpServerStatus { Pending, Connected, Failed, Disabled }

public sealed class McpServerState(string name, McpServerConfig config, string scope)
{
    public string Name { get; } = name;
    public McpServerConfig Config { get; } = config;
    public string Scope { get; } = scope;
    public McpServerStatus Status { get; set; } = McpServerStatus.Pending;
    public string? Error { get; set; }
    public McpClient? Client { get; set; }
}

/// <summary>Owns MCP server connections. Config sources: user (~/.dotcode/mcp.json), project (.mcp.json),
/// settings "mcpServers", and enabled plugins. Servers connect in parallel at startup.</summary>
public sealed class McpManager : IAsyncDisposable
{
    public List<McpServerState> Servers { get; } = [];

    public static Dictionary<string, (McpServerConfig Config, string Scope)> CollectConfigs(Settings settings, string projectRoot, IReadOnlyDictionary<string, McpServerConfig> pluginServers)
    {
        var result = new Dictionary<string, (McpServerConfig, string)>(StringComparer.OrdinalIgnoreCase);
        void AddFile(string path, string scope)
        {
            if (!File.Exists(path)) return;
            try
            {
                var cfg = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.McpConfigFile);
                if (cfg?.McpServers is null) return;
                foreach (var (name, server) in cfg.McpServers) result[name] = (server, scope);
            }
            catch (JsonException) { }
        }
        AddFile(DotCodePaths.UserMcpConfig, "user");
        foreach (var (name, server) in pluginServers) result[name] = (server, "plugin");
        if (settings.McpServers is not null)
            foreach (var (name, server) in settings.McpServers) result[name] = (server, "settings");
        AddFile(Path.Combine(projectRoot, ".mcp.json"), "project");
        return result;
    }

    public async Task ConnectAllAsync(Dictionary<string, (McpServerConfig Config, string Scope)> configs, string cwd, CancellationToken ct)
    {
        foreach (var (name, (config, scope)) in configs)
            Servers.Add(new McpServerState(name, config, scope) { Status = config.Disabled == true ? McpServerStatus.Disabled : McpServerStatus.Pending });

        await Task.WhenAll(Servers.Where(s => s.Status == McpServerStatus.Pending).Select(s => ConnectAsync(s, cwd, ct))).ConfigureAwait(false);
    }

    public async Task ConnectAsync(McpServerState state, string cwd, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("MCP_TIMEOUT") is { } t && int.TryParse(t, out var ms) ? ms / 1000.0 : 30));
            state.Client = await McpClient.ConnectAsync(state.Name, state.Config, cwd, timeout.Token).ConfigureAwait(false);
            state.Status = McpServerStatus.Connected;
            state.Error = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            state.Status = McpServerStatus.Failed;
            state.Error = ex is OperationCanceledException ? "connection timed out" : ex.Message;
        }
    }

    public IEnumerable<Tool> CreateTools() =>
        Servers.Where(s => s.Status == McpServerStatus.Connected && s.Client is not null)
               .SelectMany(s => s.Client!.Tools.Select(t => (Tool)new McpTool(s.Client!, t)))
               .Concat(Servers.Any(s => s.Client?.SupportsResources == true) ? [new ListMcpResourcesTool(this), new ReadMcpResourceTool(this)] : []);

    public string? Instructions()
    {
        var sb = new StringBuilder();
        foreach (var s in Servers)
            if (s.Client?.Instructions is { Length: > 0 } i)
                sb.Append("## ").Append(s.Name).Append('\n').Append(i.Trim()).Append("\n\n");
        return sb.Length > 0 ? sb.ToString() : null;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in Servers)
            if (s.Client is not null) await s.Client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Adds a server to user (~/.dotcode/mcp.json) or project (.mcp.json) config.</summary>
    public static string AddServer(string scope, string projectRoot, string name, McpServerConfig config)
    {
        var path = scope == "project" ? Path.Combine(projectRoot, ".mcp.json") : DotCodePaths.UserMcpConfig;
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: DotCodeJson.DocumentOptions) as JsonObject ?? new JsonObject() : new JsonObject();
        if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = new JsonObject();
        servers[name] = JsonNode.Parse(JsonSerializer.Serialize(config, SettingsJsonContext.Default.McpServerConfig));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public static bool RemoveServer(string projectRoot, string name)
    {
        var removed = false;
        foreach (var path in new[] { Path.Combine(projectRoot, ".mcp.json"), DotCodePaths.UserMcpConfig })
        {
            if (!File.Exists(path)) continue;
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: DotCodeJson.DocumentOptions) is JsonObject root && root["mcpServers"] is JsonObject servers && servers.Remove(name))
            {
                File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                removed = true;
            }
        }
        return removed;
    }
}

/// <summary>Exposes one MCP server tool to the model as <c>mcp__server__tool</c>.</summary>
public sealed class McpTool(McpClient client, McpToolInfo info) : Tool
{
    public override string Name { get; } = SafeName($"mcp__{client.Name}__{info.Name}");
    public override string Description => info.Description.Length > 0 ? info.Description : $"MCP tool {info.Name} from server {client.Name}";
    public override JsonElement InputSchema => info.InputSchema;
    public override bool IsReadOnly(JsonElement input) => info.ReadOnlyHint;
    public string ServerName => client.Name;
    public string ToolName => info.Name;

    public static string SafeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        var s = sb.ToString();
        return s.Length > 64 ? s[..64] : s;
    }

    public override string DisplayName(JsonElement input, AgentSession session)
    {
        var args = input.ValueKind == JsonValueKind.Object ? string.Join(", ", input.EnumerateObject().Take(3).Select(p => $"{p.Name}: {Short(p.Value)}")) : "";
        return $"{client.Name} - {info.Name} (MCP)({args})";
    }

    private static string Short(JsonElement v)
    {
        var s = v.ValueKind == JsonValueKind.String ? "\"" + v.GetString() + "\"" : v.GetRawText();
        return s.Length > 40 ? s[..40] + "…" : s;
    }

    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession session) => new(PermissionKind.Mcp, Name);

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        try
        {
            var (content, isError) = await client.CallToolAsync(info.Name, input, ct).ConfigureAwait(false);
            var text = string.Concat(content.OfType<TextPart>().Select(t => t.Text));
            return new ToolResult
            {
                Content = content.Count > 0 ? content : [new TextPart("(no content)")],
                IsError = isError,
                Summary = isError ? "Error" : content.OfType<ImagePart>().Any() ? "Returned image" : $"{text.Split('\n').Length} lines",
            };
        }
        catch (McpException ex)
        {
            return ToolResult.Error($"MCP error from {client.Name}: {ex.Message}");
        }
    }
}

public sealed class ListMcpResourcesTool(McpManager manager) : Tool
{
    public override string Name => "ListMcpResourcesTool";
    public override string Description => "Lists resources available from connected MCP servers. Optionally filter by server name.";
    public override JsonElement InputSchema { get; } = Schema("""{"type":"object","properties":{"server":{"type":"string","description":"Optional server name to filter by"}}}""");
    public override bool IsReadOnly(JsonElement input) => true;
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession session) => new(PermissionKind.None);

    public override Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var filter = input.GetString("server");
        var sb = new StringBuilder();
        foreach (var s in manager.Servers.Where(s => s.Client is not null && (filter is null || s.Name == filter)))
            foreach (var r in s.Client!.Resources)
                sb.Append("- [").Append(s.Name).Append("] ").Append(r.Uri).Append(" — ").Append(r.Name).Append(r.Description is null ? "" : ": " + r.Description).Append('\n');
        return Task.FromResult(ToolResult.Ok(sb.Length == 0 ? "No resources found." : sb.ToString(), "Listed resources"));
    }
}

public sealed class ReadMcpResourceTool(McpManager manager) : Tool
{
    public override string Name => "ReadMcpResourceTool";
    public override string Description => "Reads a resource from an MCP server by server name and URI.";
    public override JsonElement InputSchema { get; } = Schema("""{"type":"object","properties":{"server":{"type":"string"},"uri":{"type":"string"}},"required":["server","uri"]}""");
    public override bool IsReadOnly(JsonElement input) => true;
    public override PermissionTarget GetPermissionTarget(JsonElement input, AgentSession session) => new(PermissionKind.Mcp, "mcp__" + input.GetString("server"));

    public override async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var server = manager.Servers.FirstOrDefault(s => s.Name == input.GetString("server"));
        if (server?.Client is null) return ToolResult.Error($"MCP server '{input.GetString("server")}' is not connected");
        try
        {
            var text = await server.Client.ReadResourceAsync(input.GetString("uri") ?? "", ct).ConfigureAwait(false);
            return ToolResult.Ok(text, $"Read resource ({text.Length} chars)");
        }
        catch (McpException ex) { return ToolResult.Error(ex.Message); }
    }
}
