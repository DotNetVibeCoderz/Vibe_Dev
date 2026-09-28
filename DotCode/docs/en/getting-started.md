# Getting started

> 🇮🇩 [Baca dalam Bahasa Indonesia](../id/memulai.md)

DotCode is an agentic coding tool for your terminal — a .NET 10 re-implementation of the Claude Code experience that works with **any LLM** (Anthropic, OpenAI, Azure OpenAI, Google Gemini, DeepSeek, Ollama and any OpenAI-compatible server) and ships a **harness SDK** for .NET, TypeScript, Python, Go and Java.

*Built by Gravicode Studios, led by Kang Fadhil.*

## 1. Install

| Option | Command |
|---|---|
| Native binary (recommended) | Download `dotcode` for your OS from the releases page and put it on your `PATH` |
| .NET global tool | `dotnet tool install -g DotCode.Cli` (requires the .NET 10 runtime) |
| From source | `git clone … && cd DotCode && dotnet publish src/DotCode.Cli -c Release -r <rid> -o out` |

`<rid>` is `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` or `osx-arm64`. Publishing produces a single NativeAOT executable (~14 MB) with no runtime prerequisites.

Check the installation:

```bash
dotcode --version
dotcode doctor
```

Optional helpers: **Git** (for Git Bash on Windows and git-aware features) and **ripgrep** (`rg`, faster search; a managed fallback is built in).

## 2. Configure a model provider

The quickest path is an environment variable — DotCode auto-detects these:

| Provider | Environment variables |
|---|---|
| Anthropic | `ANTHROPIC_API_KEY` (optional `ANTHROPIC_BASE_URL`) |
| OpenAI | `OPENAI_API_KEY` (optional `OPENAI_BASE_URL`) |
| Azure OpenAI | `AZURE_OPENAI_API_KEY` + `AZURE_OPENAI_ENDPOINT` |
| Google Gemini | `GEMINI_API_KEY` (or `GOOGLE_API_KEY`) |
| DeepSeek | `DEEPSEEK_API_KEY` |
| Ollama | `OLLAMA_HOST` (e.g. `http://localhost:11434`) |

For full control use `~/.dotcode/settings.json` (see [Configuration](configuration.md)):

```jsonc
{
  "model": "azure:gpt-5-mini",
  "models": { "fast": "deepseek:deepseek-v4-flash" },
  "providers": {
    "azure":    { "type": "azure", "baseUrl": "https://<resource>.openai.azure.com/openai/v1",
                  "apiKey": "${env:AZURE_OPENAI_API_KEY}", "models": ["gpt-5-mini"] },
    "deepseek": { "type": "deepseek", "apiKey": "${env:DEEPSEEK_API_KEY}" },
    "ollama":   { "type": "ollama", "baseUrl": "http://localhost:11434", "numCtx": 32768 }
  }
}
```

Keep secrets in environment variables and reference them with `${env:NAME}` — never paste keys into files you commit.

## 3. Start a session

```bash
cd your-project
dotcode                         # interactive session
dotcode "explain this codebase"  # start with a prompt
dotcode --model deepseek:deepseek-v4-flash
```

![Welcome screen](../images/welcome.png)

Things to try first:

- `/init` — generate a `DOTCODE.md` with build commands and architecture notes for this repo
- `explain the architecture of this project`
- `add input validation to <file> and write tests`
- `!dotnet test` — run a shell command yourself (bash mode)
- `@src/Program.cs what does this do?` — attach a file
- **Shift+Tab** — cycle *default → accept edits → plan mode*
- `/model`, `/theme`, `/help`

## 4. Non-interactive use

```bash
dotcode -p "summarize the git log of the last week"
git diff | dotcode -p "review this diff" --output-format json
dotcode -p "fix the failing test" --permission-mode acceptEdits --allowedTools "Bash(dotnet test*)"
```

See [Headless mode](headless.md).

## 5. Embed it in your app

```ts
import { DotCodeClient } from "dotcode-sdk";
const client = new DotCodeClient();
const session = await client.createSession({ model: "openai:gpt-5" });
console.log((await session.send("List the TODOs in this repo")).result);
await client.close();
```

SDKs exist for .NET, TypeScript, Python, Go and Java — see [SDK](sdk.md).

## Next steps

- [Interactive mode](interactive-mode.md) — the UI, shortcuts, slash commands, themes
- [Permissions](permissions.md) — modes, rules, `--dangerously-skip-permissions`
- [Tools](tools.md) — what the agent can do
- [Extensions](extensions.md) — skills, commands, subagents, hooks, plugins, MCP
- [Providers](providers.md) — every supported LLM and its quirks
- [Architecture](architecture.md) — how DotCode is built
