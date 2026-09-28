# Observability: audit log & OpenTelemetry

> 🇮🇩 [Bahasa Indonesia](../id/observabilitas.md)

Both features are **off by default** and nothing leaves your machine unless you turn them on.

## Audit log

A tamper-evident record of what the agent did: every tool call (tool, input, permission decision, outcome, duration, model), the end of every turn (tokens, cost, stop reason) and, optionally, the user prompts.

```jsonc
// ~/.dotcode/settings.json — or managed settings to enforce it for an organization
{
  "audit": {
    "enabled": true,
    "path": "~/.dotcode/audit/audit.jsonl",   // default: ~/.dotcode/audit/audit-YYYY-MM.jsonl
    "includePrompts": false                    // also log user prompts (redacted)
  }
}
```

Each line is a JSON object:

```json
{"ts":"2026-09-28T10:51:44.59Z","event":"tool_call","session":"…","user":"dev","host":"PC-01","cwd":"C:\\src\\app",
 "tool":"Bash","tool_use_id":"…","input":"{\"command\":\"npm test\"}","mode":"auto","decision":"auto",
 "reason":"runs the project's tests","outcome":"ok","duration_ms":5321,"model":"openai:gpt-5",
 "prev":"<hash of previous entry>","hash":"<sha256(prev + entry)>"}
```

| `decision` | Meaning |
|---|---|
| `mode` | Allowed by the current permission mode / built-in defaults (read-only tools, acceptEdits, bypass) |
| `rule:<rule>` | Allowed by a matching allow rule, e.g. `rule:Bash(npm run test:*)` |
| `hook` | A PreToolUse hook returned `allow` |
| `auto` | Approved by the auto-mode classifier (`reason` holds its explanation) |
| `user:allowonce` / `user:allowsession` / `user:allowalways` | Approved in the permission prompt |
| `denied` / `rejected` / `blocked` | Deny rule or auto-mode denial / user said no / PreToolUse hook blocked it |

**Hash chain.** Every entry stores the SHA-256 of the previous entry, so editing, deleting or reordering lines breaks the chain:

```bash
dotcode audit path            # where the log is
dotcode audit verify          # ✓ …: 128 entries, chain intact   (exit code 1 when broken)
dotcode audit verify other.jsonl
```

**Redaction.** Common credentials are masked before writing: `sk-…`, `ghp_…`/`github_pat_…`, Slack `xox…`, AWS `AKIA…`, Google `AIza…`, npm/PyPI tokens, JWTs and `password=… / token: … / api_key=…` assignments. Inputs are truncated to 8,000 characters. Tool *outputs* are never logged.

> The chain proves integrity of the file as written; to protect against someone rewriting the whole file, ship it to append-only storage (SIEM, WORM bucket) — for example with a `Stop` hook or a log forwarder.

## OpenTelemetry

DotCode emits traces and metrics through the standard .NET `ActivitySource`/`Meter` named **`DotCode`**, and includes a small built-in **OTLP/HTTP (JSON)** exporter — no collector SDK needed, works in the NativeAOT binary.

```jsonc
{
  "otel": {
    "enabled": true,
    "endpoint": "http://localhost:4318",          // → /v1/traces and /v1/metrics
    "headers": { "x-api-key": "${env:OTEL_KEY}" },
    "serviceName": "dotcode",
    "logPrompts": false                            // add the (redacted) prompt to turn spans
  }
}
```

Standard variables work as well: `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS` (`k=v,k2=v2`), `OTEL_SERVICE_NAME`. Setting `"telemetry": true` or `DOTCODE_ENABLE_TELEMETRY=1` enables export with those defaults.

### Spans

| Span | Attributes |
|---|---|
| `dotcode.turn` / `dotcode.subagent` | `session.id`, `dotcode.turn.id`, `gen_ai.request.model`, `dotcode.permission.mode`, `dotcode.stop_reason`, `dotcode.model_calls`, `gen_ai.usage.input_tokens`/`output_tokens`, `dotcode.cost_usd` |
| `chat <model>` (client) | `gen_ai.system`, `gen_ai.request.model`, `gen_ai.response.finish_reasons`, token usage, `error.type` |
| `execute_tool <name>` | `gen_ai.tool.name`, `gen_ai.tool.call.id`, `dotcode.permission.decision`, `dotcode.tool.outcome` |

Model and tool spans are children of the turn span; a subagent's spans nest under the `Agent` tool call that started it.

### Metrics (cumulative)

| Metric | Unit | Attributes |
|---|---|---|
| `dotcode.tokens` | token | `gen_ai.request.model`, `gen_ai.system`, `type` (input/output/cache_read/cache_write) |
| `dotcode.cost` | USD | model, system |
| `dotcode.turns` | turn | `stop_reason` |
| `dotcode.tool.calls` | call | `tool`, `outcome`, `decision` |
| `dotcode.llm.duration` | ms (histogram) | model, `outcome` |
| `dotcode.tool.duration` | ms (histogram) | `tool` |

Spans are flushed every 5 seconds, metrics every 30 seconds and both on exit. Export failures are ignored — telemetry never breaks the agent.

Quick local try with Jaeger:

```bash
docker run --rm -p 16686:16686 -p 4318:4318 jaegertracing/all-in-one
DOTCODE_ENABLE_TELEMETRY=1 dotcode -p "summarize this repo"
# open http://localhost:16686, service "dotcode"
```

Embedding the engine in your own .NET app (`DotCode.Sdk` in-process)? Subscribe to the `DotCode` source/meter with the official OpenTelemetry SDK (`AddSource("DotCode")`, `AddMeter("DotCode")`) instead of the built-in exporter.
