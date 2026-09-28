# LLM providers

> 🇮🇩 [Bahasa Indonesia](../id/provider.md)

DotCode normalizes every provider into one message model (text, images, PDFs, tool calls, tool results, reasoning with provider-opaque signatures) and one streaming event model. Each adapter translates — and where necessary, degrades — explicitly.

| `type` | Wire protocol | Notes |
|---|---|---|
| `anthropic` | Messages API (SSE) | Explicit prompt-cache breakpoints (system, tools, conversation tail), extended thinking with signature round-trip, vision, PDF, token counting. Works with Anthropic-compatible gateways via `baseUrl` (e.g. DeepSeek's `/anthropic`), `useBearerAuth`, `betas`. |
| `openai` | Responses API (default) or Chat Completions (`"api": "chat"`) | Stateless (`store:false`) — the transcript stays in DotCode; encrypted reasoning items are round-tripped; reasoning effort; strict-schema profile. |
| `azure` | Same as `openai`, `api-key` header | `baseUrl: https://<resource>.openai.azure.com/openai/v1`; model = deployment name. |
| `gemini` | `streamGenerateContent` (SSE) | Tool schemas sanitized to Gemini's OpenAPI subset (`$ref` inlined, `anyOf` null → `nullable`, `const` → `enum`); thought signatures preserved; thinking budgets. |
| `deepseek` | Chat Completions | `reasoning_content` streamed as thinking and echoed back within a tool loop; cache-hit usage reported. |
| `ollama` | Native `/api/chat` (NDJSON) | Probes `/api/show` for tools/vision/thinking and context length; **raises `num_ctx` automatically** (Ollama's small default silently truncates agent prompts). |
| `openai-compatible` | Chat Completions | For LM Studio, vLLM, LiteLLM, OpenRouter, Groq, Together, Fireworks… Behaviour is tuned with `profile` and `quirks` — no code changes. |
| `mock` | Scripted, offline | Deterministic responses from a JSON script (tests, demos, SDK conformance). |

## Examples

```jsonc
{
  "providers": {
    "anthropic":  { "type": "anthropic", "apiKey": "${env:ANTHROPIC_API_KEY}" },
    "openai":     { "type": "openai", "apiKey": "${env:OPENAI_API_KEY}" },
    "azure":      { "type": "azure", "baseUrl": "https://myres.openai.azure.com/openai/v1", "apiKey": "${env:AZURE_OPENAI_API_KEY}", "models": ["gpt-5-mini"] },
    "gemini":     { "type": "gemini", "apiKey": "${env:GEMINI_API_KEY}" },
    "deepseek":   { "type": "deepseek", "apiKey": "${env:DEEPSEEK_API_KEY}" },
    "local":      { "type": "ollama", "baseUrl": "http://localhost:11434", "numCtx": 32768, "keepAlive": "30m" },
    "openrouter": { "type": "openai-compatible", "baseUrl": "https://openrouter.ai/api/v1", "apiKey": "${env:OPENROUTER_API_KEY}", "profile": "openrouter" },
    "lmstudio":   { "type": "openai-compatible", "baseUrl": "http://localhost:1234/v1", "profile": "lmstudio", "models": ["qwen2.5-coder-14b"] },
    "internal":   { "type": "openai-compatible", "baseUrl": "https://llm.internal/v1", "apiKey": "${env:INTERNAL_KEY}",
                    "quirks": { "roleForSystem": "system", "supportsStreamUsage": false, "maxTokensParam": "max_tokens", "reasoningField": "reasoning_content" } },
    "ds-claude-api": { "type": "anthropic", "baseUrl": "https://api.deepseek.com/anthropic", "apiKey": "${env:DEEPSEEK_API_KEY}" }
  }
}
```

### Quirk profiles

`openai`, `azure`, `deepseek`, `openrouter`, `vllm`/`sglang`, `lmstudio`/`ollama`/`llamacpp`, `groq`/`together`/`fireworks`/`litellm`/`mistral`. Individual `quirks` override the profile:

| Quirk | Meaning |
|---|---|
| `roleForSystem` | `system`, `developer` or `user` |
| `maxTokensParam` | `max_tokens` or `max_completion_tokens` |
| `supportsStreamUsage` | send `stream_options.include_usage` |
| `parallelTools` | send `parallel_tool_calls` |
| `reasoningField` | delta field with reasoning text (`reasoning_content`, `reasoning`) |
| `sendReasoningBack` | echo reasoning on assistant tool-call messages within the turn |
| `supportsTemperature`, `supportsReasoningEffort` | feature flags |
| `authHeader` | `authorization` (Bearer) or `api-key` |

### Capability overrides and prices

Capabilities (context window, max output, vision, reasoning style, caching, schema dialect) and USD prices come from a built-in catalog matched by model-id prefix. Override per model:

```jsonc
"providers": { "local": { "type": "ollama", "modelOverrides": { "qwen3-coder": { "contextWindow": 65536, "tools": true } } } }
```

## Graceful degradation

| Missing capability | What DotCode does |
|---|---|
| Native function calling | **Text tool protocol**: tool definitions go in the system prompt and `<tool_call>{…}</tool_call>` blocks are parsed tolerantly (set `"tools": false` in `modelOverrides` to force it). |
| Vision | Images are replaced with a marker. |
| Reasoning controls | `effort` is ignored for that model. |
| Token counting endpoint | Local estimate plus the provider's reported usage. |
| Malformed streamed tool arguments | JSON repair (unterminated strings/brackets, code fences); otherwise the model gets an input-validation error and retries. |

## Retries, fallback and cost

Transient errors (`rate_limit`, `overloaded`, `server_error`, network, timeouts) are retried with exponential backoff (up to 8 attempts, honoring `Retry-After`). Then a configured `fallback` chain (or `--fallback-model`) takes over. "Context too long" triggers an automatic compaction and a retry. Every call records usage; `/cost` and the exit summary show tokens and estimated USD per model.

## Recording and replay

`--record script.json` records every model response in the scripted-provider format; replay it offline with `{"type":"mock","script":"script.json"}` — useful for deterministic tests.
