using System.Text;
using System.Text.RegularExpressions;
using DotCode.Engine.Extensibility;
using DotCode.Engine.Permissions;
using DotCode.Engine.Tools.Builtin;
using DotCode.Engine.Util;

namespace DotCode.Engine.Agent;

public sealed record ExpandedPrompt(string Prompt, bool IsCommand, string? CommandName = null, string? ModelOverride = null);

/// <summary>Expands prompt-type slash commands shared by every surface (TUI, headless, SDK): custom commands
/// (commands/*.md), skills (/skill-name), plugin commands (/plugin:cmd) and MCP prompts (/mcp__server__prompt).
/// Built-in UI commands (/model, /clear…) are handled by the host.</summary>
public static partial class CommandExpander
{
    [GeneratedRegex(@"!`([^`]+)`")]
    private static partial Regex InlineBash();

    public static bool TryParse(string input, out string name, out string args)
    {
        name = args = "";
        if (!input.StartsWith('/') || input.Length < 2 || input[1] == '/' || input[1] == ' ') return false;
        var space = input.IndexOfAny([' ', '\n']);
        name = space < 0 ? input[1..] : input[1..space];
        args = space < 0 ? "" : input[(space + 1)..].Trim();
        // Paths like "/usr/bin/foo" are not commands.
        return !name.Contains('/') && !name.Contains('\\');
    }

    public static bool IsPromptCommand(AgentRuntime runtime, string name) =>
        runtime.Extensions.Commands.ContainsKey(name) || runtime.Extensions.Skills.ContainsKey(name) || name.StartsWith("mcp__", StringComparison.Ordinal);

    public static async Task<ExpandedPrompt> ExpandAsync(AgentSession session, string input, CancellationToken ct)
    {
        if (!TryParse(input, out var name, out var args)) return new ExpandedPrompt(input, false);
        var ext = session.Runtime.Extensions;

        if (ext.Commands.TryGetValue(name, out var cmd))
        {
            var body = ExtensionRegistry.ExpandArguments(cmd.Body, args);
            body = await RunInlineBashAsync(session, body, cmd.AllowedTools, ct).ConfigureAwait(false);
            var header = $"<command-name>/{name}</command-name>\n<command-args>{args}</command-args>\n\n";
            if (cmd.AllowedTools is { Count: > 0 } allowed)
                foreach (var rule in allowed) session.Permissions.AddSessionRule(rule);
            return new ExpandedPrompt(header + body, true, name, cmd.Model);
        }
        if (ext.Skills.TryGetValue(name, out var skill))
            return new ExpandedPrompt($"<command-name>/{name}</command-name>\n\n{SkillTool.Render(skill, args)}", true, name, skill.Model);

        if (name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = name.Split("__");
            if (parts.Length >= 3)
            {
                var server = session.Runtime.Mcp.Servers.FirstOrDefault(s => s.Name == parts[1])?.Client;
                var prompt = server?.Prompts.FirstOrDefault(p => p.Name == string.Join("__", parts.Skip(2)));
                if (server is not null && prompt is not null)
                {
                    var values = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var dict = prompt.Arguments.Select((a, i) => (a, v: i < values.Length ? values[i] : "")).ToDictionary(x => x.a, x => x.v);
                    var text = await server.GetPromptAsync(prompt.Name, dict, ct).ConfigureAwait(false);
                    return new ExpandedPrompt(text, true, name);
                }
            }
        }
        return new ExpandedPrompt(input, false);
    }

    /// <summary>Runs <c>!`command`</c> snippets in command templates when Bash is allowed for the command.</summary>
    private static async Task<string> RunInlineBashAsync(AgentSession session, string body, List<string>? allowedTools, CancellationToken ct)
    {
        if (!body.Contains("!`", StringComparison.Ordinal)) return body;
        var matches = InlineBash().Matches(body);
        if (matches.Count == 0) return body;
        var sb = new StringBuilder(body);
        foreach (var m in matches.Reverse())
        {
            var command = m.Groups[1].Value;
            var allowed = allowedTools?.Any(r => PermissionRule.Parse(r) is { Tool: "Bash" } rule &&
                (rule.Specifier is null || Wildcard.IsMatch(command, rule.Specifier.Replace(":*", " *")) || command.StartsWith(rule.Specifier.Replace(":*", "").Replace("*", "").Trim(), StringComparison.Ordinal))) == true;
            string output;
            if (!allowed && session.Mode != PermissionMode.BypassPermissions) output = $"[not run: add \"Bash({command})\" to allowed-tools]";
            else
            {
                var psi = new System.Diagnostics.ProcessStartInfo(ProcessRunner.BashPath ?? "bash") { WorkingDirectory = session.Cwd };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
                var result = await ProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                output = result.Output.TrimEnd();
            }
            sb.Remove(m.Index, m.Length).Insert(m.Index, output);
        }
        return sb.ToString();
    }
}
