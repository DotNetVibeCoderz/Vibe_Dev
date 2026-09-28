using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotCode.Abstractions;

namespace DotCode.Providers;

/// <summary>Graceful degradation for models without native function calling: tool definitions are injected into
/// the system prompt and the model is asked to emit <c>&lt;tool_call&gt;{json}&lt;/tool_call&gt;</c> blocks, which are
/// parsed (tolerantly) back into tool calls. Marked experimental in the UI.</summary>
public sealed partial class TextToolProtocolProvider(IModelProvider inner) : IModelProvider
{
    private const string Open = "<tool_call>";
    private const string Close = "</tool_call>";

    [GeneratedRegex(@"<tool_call>\s*(.*?)\s*</tool_call>", RegexOptions.Singleline)]
    private static partial Regex CallPattern();

    public string Id => inner.Id;
    public ModelCapabilities GetCapabilities(string modelId) => inner.GetCapabilities(modelId) with { Tools = true, ParallelToolCalls = false };
    public ValueTask<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) => inner.ListModelsAsync(ct);

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var rewritten = Rewrite(request);
        var full = new StringBuilder();
        var emitted = 0;
        var suppress = false;
        await foreach (var ev in inner.StreamAsync(rewritten, ct).ConfigureAwait(false))
        {
            switch (ev)
            {
                case TextDelta td:
                    full.Append(td.Text);
                    if (suppress) break;
                    var s = full.ToString();
                    var openAt = s.IndexOf(Open, StringComparison.Ordinal);
                    // Hold back a possible partial "<tool_call>" prefix at the end of the buffer.
                    var safeEnd = openAt >= 0 ? openAt : Math.Max(emitted, s.Length - Open.Length);
                    if (safeEnd > emitted)
                    {
                        yield return new TextDelta(s[emitted..safeEnd]);
                        emitted = safeEnd;
                    }
                    if (openAt >= 0) suppress = true;
                    break;
                case ContentBlockCompleted { Part: TextPart }:
                    break; // re-emitted below after parsing
                case MessageStopped ms:
                    var text = full.ToString();
                    var calls = new List<ToolUsePart>();
                    foreach (Match m in CallPattern().Matches(text))
                    {
                        var json = JsonRepair.TryRepair(m.Groups[1].Value);
                        var name = json.GetString("name") ?? json.GetString("tool") ?? "";
                        var input = json.GetProp("input") ?? json.GetProp("arguments") ?? json.GetProp("parameters") ?? DotCodeJson.EmptyObject;
                        if (name.Length > 0) calls.Add(new ToolUsePart("call_" + Guid.NewGuid().ToString("n")[..24], name, input.Clone()));
                    }
                    var visible = CallPattern().Replace(text, "").Trim();
                    if (!suppress && visible.Length > emitted) yield return new TextDelta(visible[Math.Min(emitted, visible.Length)..]);
                    if (visible.Length > 0) yield return new ContentBlockCompleted(new TextPart(visible));
                    foreach (var c in calls)
                    {
                        yield return new ToolUseStarted(c.Id, c.Name);
                        yield return new ContentBlockCompleted(c);
                    }
                    yield return new MessageStopped(calls.Count > 0 ? StopReason.ToolUse : ms.Reason, ms.Usage);
                    break;
                default:
                    yield return ev;
                    break;
            }
        }
    }

    private static ModelRequest Rewrite(ModelRequest r)
    {
        if (r.Tools.Count == 0) return r;
        var sb = new StringBuilder();
        sb.AppendLine("# Tools");
        sb.AppendLine("You can call tools. To call a tool, output exactly one block of the form:");
        sb.AppendLine("<tool_call>{\"name\": \"ToolName\", \"input\": { ...arguments... }}</tool_call>");
        sb.AppendLine("Then stop and wait: the result will arrive in a <tool_result> block. Call one tool at a time. When no tool is needed, answer normally.");
        sb.AppendLine();
        foreach (var t in r.Tools)
        {
            sb.Append("## ").AppendLine(t.Name);
            sb.AppendLine(t.Description.Length > 600 ? t.Description[..600] + "…" : t.Description);
            sb.Append("Input schema: ").AppendLine(SchemaSanitizer.Sanitize(t.InputSchema, JsonSchemaProfile.Minimal).GetRawText());
            sb.AppendLine();
        }

        var messages = new List<Message>(r.Messages.Count);
        foreach (var m in r.Messages)
        {
            var parts = new List<ContentPart>();
            foreach (var p in m.Content)
            {
                switch (p)
                {
                    case ToolUsePart tu:
                        parts.Add(new TextPart($"{Open}{{\"name\": \"{JsonEncodedText.Encode(tu.Name)}\", \"input\": {tu.Input.GetRawText()}}}{Close}"));
                        break;
                    case ToolResultPart tr:
                        parts.Add(new TextPart($"<tool_result{(tr.IsError ? " error=\"true\"" : "")}>\n{tr.TextContent}\n</tool_result>"));
                        break;
                    case ThinkingPart:
                        break;
                    default:
                        parts.Add(p);
                        break;
                }
            }
            messages.Add(new Message { Role = m.Role, Content = parts, ProviderId = m.ProviderId, Id = m.Id });
        }
        return r with
        {
            Tools = [],
            ToolChoice = ToolChoice.Auto,
            System = [.. r.System, new SystemBlock(sb.ToString())],
            Messages = messages,
        };
    }
}
