// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text.Json;
using AutoCode.Core.Abstractions;
using Microsoft.Extensions.AI;

namespace AutoCode.Core.Agents;

/// <summary>
/// Presents an <see cref="IAgentTool"/> to the model as an <see cref="AIFunction"/>.
///
/// EN: only the declaration crosses this boundary. Invocation deliberately does not: the agent loop
/// executes tools itself so that every call passes through hooks, the permission engine and the
/// terminal renderer. Automatic function invocation would bypass all three.
/// ID: hanya deklarasi yang melewati batas ini. Eksekusi sengaja tidak, karena agent loop menjalankan
/// tool sendiri agar setiap pemanggilan melewati hook, mesin izin, dan renderer terminal.
/// </summary>
public sealed class ToolFunctionAdapter(IAgentTool tool) : AIFunction
{
    public IAgentTool Tool { get; } = tool;

    public override string Name => Tool.Name;

    public override string Description => Tool.Description;

    public override JsonElement JsonSchema => Tool.InputSchema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            $"'{Name}' must be invoked through AgentLoop so that hooks and permissions apply. " +
            "Do not wrap this client with automatic function invocation.");
}
