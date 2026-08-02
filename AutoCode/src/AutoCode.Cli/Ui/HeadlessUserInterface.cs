// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text;
using System.Text.Json;
using AutoCode.Core.Abstractions;
using AutoCode.Core.Permissions;

namespace AutoCode.Cli.Ui;

/// <summary>
/// The surface used by <c>--print</c> and by <c>--output-format stream-json</c>.
///
/// EN: nothing here ever blocks on the user, because there is no user attached. Permission requests
/// are settled from policy: allowed when the run explicitly opted out of prompting, refused otherwise,
/// with a refusal message the model can act on rather than a silent failure.
/// ID: tidak ada yang menunggu masukan pengguna, karena tidak ada pengguna. Permintaan izin dijawab
/// oleh kebijakan: diizinkan bila mode tanpa prompt dipilih, ditolak bila tidak, dengan pesan yang
/// bisa ditindaklanjuti model.
/// </summary>
public sealed class HeadlessUserInterface(OutputFormat format, bool autoApprove) : IAgentUserInterface
{
    // snake_case throughout, so the payload keys match the "type" discriminator and the stream is a
    // stable contract for anything piping Auto Code into jq or another process.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly StringBuilder _text = new();
    private readonly Lock _gate = new();

    /// <summary>Everything the assistant said this run.</summary>
    public string AssistantText
    {
        get
        {
            lock (_gate)
                return _text.ToString();
        }
    }

    /// <summary>Tool calls that were refused, reported in the JSON envelope so failures are visible.</summary>
    public List<string> Denials { get; } = [];

    public ValueTask EmitAsync(AgentEvent evt, CancellationToken cancellationToken)
    {
        if (evt is AssistantTextEvent { IsFinal: false } text)
        {
            lock (_gate)
                _text.Append(text.Text);

            if (format == OutputFormat.Text)
                Console.Write(text.Text);
        }

        if (evt is ToolCallDeniedEvent denied)
            Denials.Add($"{denied.ToolName}: {denied.Reason}");

        if (format == OutputFormat.StreamJson)
            Console.WriteLine(JsonSerializer.Serialize(evt, JsonOptions));

        return ValueTask.CompletedTask;
    }

    public ValueTask<PermissionDecision> RequestPermissionAsync(
        PermissionRequest request,
        CancellationToken cancellationToken)
    {
        if (autoApprove)
            return ValueTask.FromResult(PermissionDecision.Allow);

        return ValueTask.FromResult(PermissionDecision.Deny(
            $"'{request.ToolName}' needs approval, but this run is non-interactive. " +
            "Re-run with --permission-mode acceptEdits, or add an allow rule in settings, " +
            "or find an approach that does not need this tool."));
    }
}
