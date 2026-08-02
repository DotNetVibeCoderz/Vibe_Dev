# Configuration

> Auto Code — Gravicode Studios, led by Kang Fadhil

## Where settings come from

Sources are layered. Later ones override earlier ones.

| # | Source | Purpose |
| --- | --- | --- |
| 1 | Built-in vendor presets | Sensible defaults for known providers |
| 2 | `app.config` / `autocode.dll.config` `<appSettings>` | Classic .NET deployments |
| 3 | `~/.autocode/settings.json` | Your preferences, every project |
| 4 | `<workspace>/.autocode/settings.json` | The project's settings, committed |
| 5 | `<workspace>/.autocode/settings.local.json` | Your overrides, gitignored |
| 6 | `AUTOCODE_*` environment variables | CI and containers |
| 7 | Command-line flags | This run only |

The practical consequence: commit layer 4, keep secrets in layer 5 or 6.

## Finding the workspace root

Auto Code walks up from the current directory until it finds `.git/`, `.autocode/`, `AUTOCODE.md` or
`CLAUDE.md`. That directory becomes the workspace root: settings, sessions, skills and agents all
resolve against it. Override with `--cwd`.

## The settings file

```jsonc
{
  // Which provider profile to use. Defaults to the first one that has a usable API key.
  "activeProvider": "openai",

  "providers": {
    "openai": {
      "kind": "OpenAICompatible",           // OpenAICompatible | Anthropic | Gemini
      "endpoint": "https://api.openai.com/v1",
      "apiKey": "env:OPENAI_API_KEY",       // "env:NAME" resolves at use time
      "model": "gpt-4.1",
      "smallModel": "gpt-4.1-mini",         // used for compaction and background work
      "embeddingModel": "text-embedding-3-small",
      "embeddingDimensions": 1536,
      "temperature": 0.0,
      "maxOutputTokens": 8192,
      "contextWindow": 1000000,             // drives the auto-compaction threshold
      "enableExtendedThinking": false,
      "thinkingBudgetTokens": 8000,
      "supportsParallelToolCalls": true,    // set false for some local runtimes
      "inputCostPerMillionTokens": 2.0,     // 0 disables cost display
      "outputCostPerMillionTokens": 8.0,
      "timeoutSeconds": 600,
      "headers": { "HTTP-Referer": "https://example.com" }
    }
  },

  "permissionMode": "ask",                  // ask | acceptEdits | plan | bypassPermissions
  "permissions": {
    "allow": ["Read", "Grep", "Glob", "Bash(git status)"],
    "ask":   ["Bash(git push:*)"],
    "deny":  ["Read(**/.env)", "Bash(rm -rf:*)"]
  },

  "maxTurnIterations": 100,                 // cap on tool-call rounds per user message
  "compactionThreshold": 0.82,              // fraction of the context window that triggers compaction
  "persistSessions": true,
  "showCost": true,
  "showThinking": true,
  "language": "en",                         // en | id — interface and reply language

  "verifyCommands": ["dotnet build", "dotnet test"],
  "contextFiles": ["docs/conventions.md"],
  "skillDirectories": ["tools/skills"],
  "plugins": ["tools/autocode-plugins"],
  "disabledTools": ["WebFetch"],

  "shell": "pwsh",                          // default: pwsh/powershell on Windows, $SHELL elsewhere
  "bashTimeoutMs": 120000,

  "enableSemanticIndex": false,
  "embeddings": {                           // configured separately from chat — see providers.md
    "kind": "Auto",                         // Auto | Ollama | Onnx | OpenAICompatible | None
    "model": "nomic-embed-text",            // Ollama / OpenAI-compatible
    "endpoint": "http://localhost:11434",   // Ollama default
    "modelPath": "models/model.onnx",       // Onnx only
    "vocabPath": "models/vocab.txt",        // Onnx only
    "maxTokens": 512,                       // Onnx only
    "lowerCase": true,                      // Onnx only — false for a -cased model
    "batchSize": 64,
    "dimensions": 0                         // 0 = infer from the first batch. Leave it at 0.
  },

  "agents": {},
  "teams": {},
  "hooks": {},
  "mcpServers": {}
}
```

Every key is optional. An empty `{}` is valid and falls back to the presets.

## Secrets

Never put a live key in a committed file. Use one of:

```jsonc
// .autocode/settings.json — committed
{ "providers": { "openai": { "apiKey": "env:OPENAI_API_KEY" } } }
```

```jsonc
// .autocode/settings.local.json — gitignored
{ "providers": { "openai": { "apiKey": "sk-actual-key" } } }
```

```bash
export AUTOCODE_PROVIDERS__OPENAI__APIKEY=sk-actual-key
```

`env:NAME` is resolved every time the key is read, not at load time — so rotating the variable takes
effect without restarting.

## Environment variables

Standard .NET configuration binding applies: `__` is the section separator.

```bash
AUTOCODE_ACTIVEPROVIDER=deepseek
AUTOCODE_PERMISSIONMODE=acceptEdits
AUTOCODE_PROVIDERS__DEEPSEEK__MODEL=deepseek-reasoner
```

Two shorthands exist because they are used constantly:

```bash
AUTOCODE_PROVIDER=ollama      # same as --provider
AUTOCODE_MODEL=qwen2.5-coder  # same as --model
```

## app.config

For environments that manage .NET applications through `app.config`, keys under `<appSettings>` are
read using the same configuration paths. Both `:` and `.` work as separators.

```xml
<configuration>
  <appSettings>
    <add key="AutoCode:ActiveProvider" value="openai" />
    <add key="AutoCode:Providers:openai:Model" value="gpt-4.1" />
    <add key="AutoCode:Providers:openai:ApiKey" value="env:OPENAI_API_KEY" />
    <add key="AutoCode:PermissionMode" value="Ask" />
  </appSettings>
</configuration>
```

This is the **lowest** precedence layer after presets, so a `settings.json` or an environment
variable still wins.

## Inspecting what is in effect

```bash
autocode config show     # resolved settings and where they came from
autocode config path     # the files that are being read
autocode config init     # scaffold a project settings file
autocode doctor          # verify the whole path, including a live request
```

Inside a session, `/status` shows the same thing plus the live session state.
