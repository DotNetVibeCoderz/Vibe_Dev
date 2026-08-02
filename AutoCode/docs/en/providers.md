# Providers and models

> Auto Code — Gravicode Studios, led by Kang Fadhil

Auto Code speaks three wire protocols. Every vendor maps onto one of them.

| `kind` | Protocol | Providers |
| --- | --- | --- |
| `OpenAICompatible` *(default)* | OpenAI chat completions | OpenAI, Azure OpenAI, DeepSeek, Qwen, Ollama, LM Studio, vLLM, OpenRouter, Groq, Together, Mistral, xAI, and anything else exposing `/chat/completions` |
| `Anthropic` | Anthropic Messages API | Claude, including extended thinking |
| `Gemini` | Google `streamGenerateContent` | Gemini |

Everything above the provider layer — the agent loop, the tools, subagents — works purely against
`IChatClient`. Adding a vendor is configuration, not code.

## Built-in presets

Naming a preset gives you endpoint, models and pricing; you supply the key.

| Name | Default model | Key variable |
| --- | --- | --- |
| `openai` | `gpt-4.1` | `OPENAI_API_KEY` |
| `anthropic` | `claude-sonnet-4-5` | `ANTHROPIC_API_KEY` |
| `gemini` | `gemini-2.5-pro` | `GEMINI_API_KEY` |
| `deepseek` | `deepseek-chat` | `DEEPSEEK_API_KEY` |
| `qwen` | `qwen-max` | `DASHSCOPE_API_KEY` |
| `ollama` | `qwen2.5-coder:14b` | — (local) |
| `lmstudio` | `local-model` | — (local) |
| `openrouter` | `anthropic/claude-sonnet-4.5` | `OPENROUTER_API_KEY` |
| `groq` | `llama-3.3-70b-versatile` | `GROQ_API_KEY` |
| `mistral` | `mistral-large-latest` | `MISTRAL_API_KEY` |
| `xai` | `grok-4` | `XAI_API_KEY` |
| `azure-openai` | `gpt-4.1` | `AZURE_OPENAI_API_KEY` |

Anything you set in a profile overrides the preset; anything you leave out falls back to it.

## Any other endpoint

If it accepts `POST /chat/completions` in OpenAI's shape, it works:

```jsonc
{
  "providers": {
    "my-gateway": {
      "kind": "OpenAICompatible",
      "endpoint": "https://llm.internal.example.com/v1",
      "apiKey": "env:INTERNAL_LLM_KEY",
      "model": "internal-coder-v2",
      "contextWindow": 65536,
      "supportsParallelToolCalls": false
    }
  }
}
```

## Switching at runtime

```bash
autocode --provider anthropic --model claude-opus-4-5
```

```
› /provider              # list profiles, marking the active one
› /provider deepseek     # switch
› /model deepseek-reasoner
```

Switching rebuilds the agent loop around the new profile. The conversation carries over; the cost
counter restarts, because pricing changed.

## Model roles

A profile can name three models:

| Field | Used for |
| --- | --- |
| `model` | The main agent loop |
| `smallModel` | Compaction summaries and background work — cheaper, faster |
| `embeddingModel` | The semantic code index |

Setting `smallModel` to something cheap noticeably reduces the cost of long sessions, because
compaction runs on it rather than on your main model.

## Embeddings

Embeddings are configured **separately from chat**, because the two rarely want the same backend.
Anthropic has no embedding endpoint at all, and even where a provider offers one, sending every
source file in a repository through a paid API costs real money to produce a result a local model
matches.

Four backends, chosen with `embeddings.kind`:

| Kind | Needs | Use when |
| --- | --- | --- |
| `Auto` *(default)* | — | Follow the chat provider; resolves to `None` if it has no embedding endpoint |
| `Ollama` | Ollama running locally | You already run Ollama. Free, private, good quality |
| `Onnx` | A `.onnx` file on disk | No server and no network at all |
| `OpenAICompatible` | An API key | You want a hosted embedding model |
| `None` | — | Disable the index |

### Ollama

Uses Ollama's native `/api/embed` through OllamaSharp — not the OpenAI compatibility shim.

```bash
ollama pull nomic-embed-text
```

```jsonc
{
  "enableSemanticIndex": true,
  "embeddings": { "kind": "Ollama", "model": "nomic-embed-text" }
}
```

Defaults to `http://localhost:11434`. An endpoint written with a `/v1` suffix is accepted and
trimmed, since that is what you will have copied from the chat provider.

Good code-embedding models: `nomic-embed-text` (768d), `mxbai-embed-large` (1024d),
`all-minilm` (384d, fastest).

### ONNX — fully offline

No server, no network, no key. Download a sentence-transformer once and point at the files:

```bash
mkdir -p models && cd models
curl -LO https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx
curl -LO https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt
```

```jsonc
{
  "enableSemanticIndex": true,
  "embeddings": {
    "kind": "Onnx",
    "modelPath": "models/model.onnx",
    "vocabPath": "models/vocab.txt",
    "maxTokens": 256
  }
}
```

That model is 90 MB and produces 384-dimensional vectors in roughly 20 ms per text on CPU. Auto Code
mean-pools the token states over the attention mask and L2-normalises the result, which is what
sentence-transformer models are trained against.

Any BERT-style model exported with the standard `input_ids` / `attention_mask` / `token_type_ids`
inputs works. Set `lowerCase: false` for a `-cased` model.

### Mixing backends

This is the point of the separation — reason with a hosted model, index locally:

```jsonc
{
  "activeProvider": "anthropic",
  "providers": { "anthropic": { "kind": "Anthropic", "model": "claude-sonnet-4-5" } },

  "enableSemanticIndex": true,
  "embeddings": { "kind": "Ollama", "model": "nomic-embed-text" }
}
```

### Dimensions

Leave `embeddings.dimensions` unset. Auto Code learns the width from the first batch.

Stating a wrong dimension used to be a silent failure — every similarity comparison is skipped and
the index returns nothing, with no error anywhere. If you do set it, it is enforced as an assertion
and a mismatch fails loudly.

Verify the backend before you rely on it:

```bash
autocode doctor      # constructs the backend and reports it
```

```
› /index             # builds the index, reporting dimensions
› /status            # shows the resolved backend
```

## Extended thinking

```jsonc
{
  "providers": {
    "anthropic": {
      "kind": "Anthropic",
      "model": "claude-sonnet-4-5",
      "enableExtendedThinking": true,
      "thinkingBudgetTokens": 12000
    }
  }
}
```

Reasoning is streamed to the terminal in dim italics; turn the display off with
`"showThinking": false` while leaving the reasoning itself on.

For Anthropic, thinking blocks are replayed with their signatures across tool-use round trips, which
is what the API requires — a detail that generic adapters commonly drop.

## Local models

Local runtimes are first-class. Two things matter:

- **Tool calling.** The model has to support it. `qwen2.5-coder`, `llama3.1`+ and `mistral-nemo` do;
  many smaller models do not, and will simply never call a tool.
- **Parallel calls.** Several local servers reject batched tool calls. Set
  `"supportsParallelToolCalls": false` if you see malformed-request errors.

```jsonc
{
  "activeProvider": "ollama",
  "providers": {
    "ollama": {
      "endpoint": "http://localhost:11434/v1",
      "model": "qwen2.5-coder:14b",
      "contextWindow": 32768,
      "supportsParallelToolCalls": false
    }
  }
}
```

## Cost tracking

Set the two price fields and Auto Code tracks spend per session:

```jsonc
{ "inputCostPerMillionTokens": 3.0, "outputCostPerMillionTokens": 15.0 }
```

`/cost` breaks it down; the status line after each turn shows a running total. Leave them at `0` —
as the local presets do — and the display shows tokens only.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| `API key is not configured` | The `env:` variable is unset. Check with `autocode doctor`. |
| `404` on an OpenAI-compatible endpoint | `endpoint` is missing the `/v1` suffix. |
| The model never calls a tool | It does not support tool calling. Try a larger or newer model. |
| `400` with a schema complaint on Gemini | Report it: schema sanitisation missed a keyword. |
| Repeated malformed-request errors | Set `"supportsParallelToolCalls": false`. |
| Context fills up too fast | Lower `compactionThreshold`, or set a correct `contextWindow`. |
