# LLM providers

> 🇮🇩 [Bahasa Indonesia](../id/provider.md)

DotCode normalizes every provider into one message model (text, images, PDFs, tool calls, tool results, reasoning with provider-opaque signatures) and one streaming event model. Each adapter translates — and where necessary, degrades — explicitly.

| `type` | Wire protocol | Notes |
|---|---|---|
| `anthropic` | Messages API (SSE) | Explicit prompt-cache breakpoints (system, tools, conversation tail), extended thinking with signature round-trip, vision, PDF, token counting. Works with Anthropic-compatible gateways via `baseUrl` (e.g. DeepSeek's `/anthropic`), `useBearerAuth`, `betas`. |
| `openai` | Responses API (default) or Chat Completions (`"api": "chat"`) | Stateless (`store:false`) — the transcript stays in DotCode; encrypted reasoning items are round-tripped; reasoning effort; strict-schema profile. |
| `bedrock` | Anthropic Messages on Amazon Bedrock (AWS event stream) | SigV4 with AWS credentials (env, `~/.aws` profiles, `credential_process`, SSO via the AWS CLI) or a Bedrock API key. See [Cloud platforms](#cloud-platforms). |
| `vertex` | Anthropic Messages on Google Vertex AI (SSE) | OAuth from Application Default Credentials. See [Cloud platforms](#cloud-platforms). |
| `azure` | Same as `openai`, `api-key` header | `baseUrl: https://<resource>.openai.azure.com/openai/v1`; model = deployment name. Or `"auth": "entra"` for Microsoft Entra ID tokens instead of a key. |
| `gemini` | `streamGenerateContent` (SSE) | Tool schemas sanitized to Gemini's OpenAPI subset (`$ref` inlined, `anyOf` null → `nullable`, `const` → `enum`); thought signatures preserved; thinking budgets. `"platform": "vertex"` (or `type: vertex-gemini`) calls Gemini through Vertex AI with OAuth. |
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

## Cloud platforms

### Amazon Bedrock (Claude)

```jsonc
"providers": { "bedrock": { "type": "bedrock", "region": "us-east-1", "awsProfile": "work" } }   // model: bedrock:sonnet
```

- **Credentials**, first match wins:
  1. `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` (+ `AWS_SESSION_TOKEN`);
  2. the `awsProfile` (or `AWS_PROFILE`) in `~/.aws/credentials` or `~/.aws/config`, including `credential_process`;
  3. `aws configure export-credentials`, which covers SSO (`aws sso login`) and assumed-role profiles.

  Requests are signed with SigV4. A **Bedrock API key** (`apiKey` or `AWS_BEARER_TOKEN_BEDROCK`) is sent as a bearer token instead.
- **Region:** `region`, `AWS_REGION` / `AWS_DEFAULT_REGION`, the profile's region, else `us-east-1`.
- **Models:** use the Bedrock model id or inference profile, e.g. `us.anthropic.claude-sonnet-4-5-20250929-v1:0`. `bedrock:sonnet`, `bedrock:opus` and `bedrock:haiku` map to the current US inference profiles. The bare aliases `sonnet` / `opus` / `haiku` use Bedrock automatically when no direct Anthropic provider is configured. `betas` are sent as `anthropic_beta`.
- **Claude Code compatible:** `CLAUDE_CODE_USE_BEDROCK=1` (or `DOTCODE_USE_BEDROCK=1`) adds the provider automatically. `ANTHROPIC_MODEL` sets the default model and `ANTHROPIC_BEDROCK_BASE_URL` overrides the endpoint.
- **Errors:** event-stream exceptions map to DotCode's retry logic. `throttlingException` is retried like a rate limit.

### Google Vertex AI (Claude and Gemini)

```jsonc
"providers": {
  "vertex":        { "type": "vertex", "project": "my-project", "region": "us-east5" },        // Claude: vertex:claude-sonnet-4-5@20250929 or vertex:sonnet
  "gemini-vertex": { "type": "gemini", "platform": "vertex", "project": "my-project", "region": "global" }   // Gemini: gemini-vertex:gemini-2.5-pro
}
```

- **Credentials** (Application Default Credentials), first match wins:
  1. `GOOGLE_OAUTH_ACCESS_TOKEN`;
  2. `credentialsFile` / `GOOGLE_APPLICATION_CREDENTIALS`: a service-account key is exchanged with a signed RS256 JWT, and an authorized-user file from `gcloud auth application-default login` uses its refresh token;
  3. the metadata server on GCE, GKE or Cloud Run;
  4. `gcloud auth print-access-token`.

  Tokens are cached and refreshed before they expire.
- **Project:** `project`, `ANTHROPIC_VERTEX_PROJECT_ID`, `GOOGLE_CLOUD_PROJECT`, or the service account's project.
- **Location:** `region`, `CLOUD_ML_REGION`, `GOOGLE_CLOUD_LOCATION` (Claude defaults to `us-east5`, Gemini to `global`).
- **Claude Code compatible:** `CLAUDE_CODE_USE_VERTEX=1` (or `DOTCODE_USE_VERTEX=1`) with `ANTHROPIC_VERTEX_PROJECT_ID` and `CLOUD_ML_REGION`.

### Azure OpenAI with Microsoft Entra ID

```jsonc
"providers": { "azure": { "type": "azure", "baseUrl": "https://myres.openai.azure.com/openai/v1", "auth": "entra", "models": ["gpt-5-mini"] } }
```

- **Credentials** (like `DefaultAzureCredential`), first match wins:
  1. a service principal (`AZURE_TENANT_ID` + `AZURE_CLIENT_ID` + `AZURE_CLIENT_SECRET`, or `tenantId` / `clientId` / `clientSecret` in settings);
  2. workload identity (`AZURE_FEDERATED_TOKEN_FILE`);
  3. managed identity (App Service / Container Apps endpoint, or the VM's IMDS);
  4. the Azure CLI (`az login`).
- **Token:** requested for `https://cognitiveservices.azure.com/.default` (`scope` overrides it), cached, and sent as a bearer token instead of `api-key`.
- **From the environment:** with `AZURE_OPENAI_ENDPOINT` set and no `AZURE_OPENAI_API_KEY`, setting `AZURE_OPENAI_USE_ENTRA=1` or `AZURE_CLIENT_ID` configures this automatically.

> These integrations follow the public AWS SigV4 / event-stream, Google OAuth and Microsoft identity specifications. They are covered by tests (the official SigV4 test vector, JWT signature verification, mocked token endpoints and wire formats). They have not yet been exercised against live AWS, Google Cloud or Azure accounts; please report any issue you hit.

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
