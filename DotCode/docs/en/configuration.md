# Configuration

> 🇮🇩 [Bahasa Indonesia](../id/konfigurasi.md)

## Settings files and precedence

Settings are JSON (comments and trailing commas allowed) and are **deep-merged** from lowest to highest precedence:

| # | Scope | Path |
|---|---|---|
| 1 | User | `~/.dotcode/settings.json` |
| 2 | Project (shared) | `.claude/settings.json` (compat), `.dotcode/settings.json` |
| 3 | Local (not committed) | `.claude/settings.local.json` (compat), `.dotcode/settings.local.json` |
| 4 | CLI | `--settings <file-or-inline-json>` |
| 5 | Managed (organization policy) | Windows `%ProgramData%\DotCode\managed-settings.json`, macOS `/Library/Application Support/DotCode/`, Linux `/etc/dotcode/` |

Objects merge recursively, scalars are overridden, and permission lists (`allow`, `deny`, `ask`, `additionalDirectories`) are concatenated. `DOTCODE_CONFIG_DIR` relocates the user directory. Inspect the result with `dotcode config list`; change values with `dotcode config set [-g] <key> <json-or-string>`.

For migration, DotCode also reads Claude Code's project `.claude/settings*.json` (Claude-specific keys like `model` are ignored), `CLAUDE.md`, `.claude/skills|commands|agents` and `~/.claude/skills|commands|agents`.

## Reference

```jsonc
{
  // Main model: provider:model, an alias (sonnet, opus, haiku, gpt, gemini, deepseek) or a role
  "model": "anthropic:claude-sonnet-4-5",

  // Models per role. "fast" is used for titles, WebFetch summaries and the Explore subagent,
  // "planner" by the Plan subagent, "subagent" for general subagents.
  "models": { "main": "...", "fast": "...", "planner": "...", "subagent": "...", "advisor": "..." },

  // Named provider instances (any number; the name is the prefix in "name:model")
  "providers": { "work-azure": { "type": "azure", "baseUrl": "...", "apiKey": "${env:AZURE_KEY}" } },

  // Fallback chains on transient errors (after retries); switch happens at a safe turn boundary
  "fallback": [ { "on": ["rate_limit", "overloaded", "5xx"], "chain": ["openai:gpt-5", "deepseek:deepseek-chat"] } ],

  "permissions": {
    "allow": ["Bash(npm run test:*)", "Read(~/notes/**)", "WebFetch(domain:learn.microsoft.com)"],
    "ask":   ["Bash(git push:*)"],
    "deny":  ["Read(./.env)", "Bash(rm -rf *)"],
    "defaultMode": "default",               // default | acceptEdits | plan | bypassPermissions
    "additionalDirectories": ["../shared-lib"],
    "disableBypassPermissionsMode": false   // set true in managed settings to forbid bypass
  },

  "hooks": { "PostToolUse": [ { "matcher": "Edit|Write", "hooks": [ { "type": "command", "command": "dotnet format" } ] } ] },
  "env": { "DOTNET_CLI_TELEMETRY_OPTOUT": "1" },   // applied to the process when not already set
  "mcpServers": { "github": { "type": "http", "url": "https://api.githubcopilot.com/mcp/", "headers": { "Authorization": "Bearer ${env:GH_TOKEN}" } } },
  "enabledPlugins": { "gravicode-toolkit@gravicode": true },

  "theme": "dark",                           // see /theme and `dotcode theme list`
  "tui": {
    "glyphs": "unicode",                     // unicode | ascii | nerd  (match your terminal font)
    "border": "lines",                       // lines | rounded | single | double | heavy (input box style)
    "spinner": "claude",                     // claude | dots | line | star | bounce | arc | dotnet
    "accent": "#D77757",                     // override the theme accent color
    "reducedMotion": false,
    "showThinking": false,
    "showTips": true
  },
  "spinnerVerbs": ["Brewing", "Compiling"],  // custom spinner verbs
  "statusLine": { "type": "command", "command": "~/.dotcode/statusline.sh" },

  "outputStyle": "default",                  // default | explanatory | learning | custom name
  "effort": "medium",                        // off | low | medium | high | xhigh
  "maxOutputTokens": 32000,
  "maxTurns": 100,
  "autoCompact": true,
  "autoCompactThreshold": 0.85,
  "promptCaching": true,
  "budget": { "maxUsdPerSession": 5, "action": "warn" },
  "allowedProviders": ["ollama"],            // organization policy: only these providers
  "webSearch": { "provider": "tavily", "apiKey": "${env:TAVILY_API_KEY}" }
}
```

## Model references

| Form | Example | Meaning |
|---|---|---|
| `provider:model` | `deepseek:deepseek-v4-flash` | Explicit provider instance and model |
| Bare model | `gpt-5-mini` | Model on the first configured provider |
| Alias | `sonnet`, `opus`, `haiku`, `gpt`, `gemini`, `deepseek` | Mapped to a sensible default on a provider of that type |
| Role | `fast`, `planner`, `subagent` | Resolved through `models.<role>` (falls back to main) |
| Ollama tags | `ollama:qwen3:8b` | Only the first colon separates provider and model |

## Memory files (DOTCODE.md)

Loaded into every conversation, in this order: managed `DOTCODE.md`; user `~/.dotcode/DOTCODE.md` and `~/.claude/CLAUDE.md`; then for every directory from the project root down to the working directory: `DOTCODE.md`, `.dotcode/DOTCODE.md`, `CLAUDE.md`, `.claude/CLAUDE.md`, `AGENTS.md`, and private `DOTCODE.local.md` / `CLAUDE.local.md`. Files can import others with `@relative/path.md`. Add a memory quickly by starting a prompt with `#`, or run `/memory`.

## Files and data

| Path | Content |
|---|---|
| `~/.dotcode/projects/<project>/<session>.jsonl` | Session transcripts (resume with `--continue` / `--resume`) |
| `~/.dotcode/projects/<project>/checkpoints/` | File snapshots used by `/rewind` |
| `~/.dotcode/history.jsonl` | Prompt history (↑/↓) |
| `~/.dotcode/mcp.json` | User-scope MCP servers (`dotcode mcp add`) |
| `.mcp.json` | Project-scope MCP servers |
| `~/.dotcode/plugins/` | Installed plugins and marketplaces |
| `~/.dotcode/themes/*.json` | Custom themes |
