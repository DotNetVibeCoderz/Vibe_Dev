// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text.Json;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Configuration;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace AutoCode.Mcp;

/// <summary>Outcome of trying to bring up one configured MCP server.</summary>
public sealed record McpConnectionResult(string ServerName, bool Connected, int ToolCount, string? Error);

/// <summary>
/// Connects the MCP servers a workspace declares and exposes their tools to the agent.
///
/// EN: MCP tools arrive namespaced as <c>mcp__server__tool</c>. The prefix is not decoration — it
/// keeps a server from shadowing a built-in tool, and it makes permission rules like
/// <c>mcp__github__*</c> expressible. A server that fails to start is reported and skipped, never
/// fatal: a broken integration should not cost the user their session.
/// ID: tool MCP diberi awalan <c>mcp__server__tool</c> agar tidak menimpa tool bawaan dan agar aturan
/// izin seperti <c>mcp__github__*</c> bisa ditulis. Server yang gagal dijalankan dilewati, tidak
/// mematikan sesi.
/// </summary>
public sealed class McpServerManager : IAsyncDisposable
{
    private readonly List<McpClient> _clients = [];
    private readonly Lock _gate = new();

    /// <summary>Servers that came up successfully.</summary>
    public IReadOnlyList<McpConnectionResult> Connections { get; private set; } = [];

    /// <summary>
    /// Starts every enabled server and registers its tools.
    /// </summary>
    public async Task<IReadOnlyList<McpConnectionResult>> ConnectAsync(
        AutoCodeOptions options,
        IToolRegistry registry,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var results = new List<McpConnectionResult>();

        foreach (var (name, server) in options.McpServers)
        {
            if (server.Disabled)
                continue;

            try
            {
                var transport = CreateTransport(name, server, workspaceRoot);
                var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);

                lock (_gate)
                    _clients.Add(client);

                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                foreach (var tool in tools)
                    registry.Register(new McpToolAdapter(name, tool));

                results.Add(new McpConnectionResult(name, true, tools.Count, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new McpConnectionResult(name, false, 0, ex.Message));
            }
        }

        Connections = results;
        return results;
    }

    private static IClientTransport CreateTransport(string name, McpServerOptions server, string workspaceRoot)
    {
        if (server.Transport.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            server.Transport.Equals("sse", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(server.Url))
                throw new InvalidOperationException($"MCP server '{name}' uses http transport but has no url.");

            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = name,
                Endpoint = new Uri(server.Url),
                AdditionalHeaders = server.Headers.Count > 0 ? server.Headers : null,
            });
        }

        if (string.IsNullOrWhiteSpace(server.Command))
            throw new InvalidOperationException($"MCP server '{name}' uses stdio transport but has no command.");

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = name,
            Command = server.Command,
            Arguments = server.Args,
            WorkingDirectory = workspaceRoot,
            EnvironmentVariables = server.Env.Count > 0
                ? server.Env.ToDictionary(e => e.Key, e => (string?)e.Value, StringComparer.Ordinal)
                : null,
        });
    }

    public async ValueTask DisposeAsync()
    {
        List<McpClient> clients;

        lock (_gate)
        {
            clients = [.. _clients];
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A server that died on its own cannot be shut down cleanly, and does not need to be.
            }
        }
    }
}

/// <summary>Presents an MCP tool as an Auto Code tool, so it passes through the same permission gates.</summary>
internal sealed class McpToolAdapter(string serverName, McpClientTool tool) : IAgentTool
{
    public string Name { get; } = $"mcp__{Sanitize(serverName)}__{Sanitize(tool.Name)}";

    public string Description => string.IsNullOrWhiteSpace(tool.Description)
        ? $"MCP tool '{tool.Name}' provided by server '{serverName}'."
        : $"{tool.Description}\n\n(Provided by MCP server '{serverName}'.)";

    public JsonElement InputSchema => tool.JsonSchema;

    /// <summary>
    /// An MCP server can do anything, and the protocol does not tell us what. Declaring the widest
    /// capability is the safe default: it means every MCP call is subject to approval unless the
    /// user has explicitly allow-listed it.
    /// </summary>
    public ToolCapability Capability =>
        ToolCapability.ReadsFiles | ToolCapability.WritesFiles |
        ToolCapability.ExecutesCommands | ToolCapability.AccessesNetwork;

    public string Summarize(JsonElement arguments) => $"{Name}(…)";

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        try
        {
            var arguments = new AIFunctionArguments();

            if (invocation.Arguments.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in invocation.Arguments.EnumerateObject())
                    arguments[property.Name] = property.Value;
            }

            var result = await tool.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            var text = Stringify(result);

            return ToolResult.Ok(text, $"{Name} → {text.Length:N0} chars");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"MCP server '{serverName}' failed to run '{tool.Name}': {ex.Message}");
        }
    }

    private static string Stringify(object? result) => result switch
    {
        null => "(no output)",
        string s => s,
        JsonElement element => element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : element.GetRawText(),
        _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions),
    };

    /// <summary>Tool names must survive every provider's naming rules, which are stricter than MCP's.</summary>
    private static string Sanitize(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];

        for (var i = 0; i < value.Length; i++)
            buffer[i] = char.IsLetterOrDigit(value[i]) || value[i] == '_' ? value[i] : '_';

        return new string(buffer);
    }
}
