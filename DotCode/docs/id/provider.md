# Provider LLM

> 🇬🇧 [English](../en/providers.md)

DotCode menormalkan semua provider ke satu model pesan (teks, gambar, PDF, pemanggilan tool, hasil tool, reasoning dengan tanda tangan milik provider) dan satu model event streaming. Setiap adapter menerjemahkan — dan bila perlu mendegradasi — secara eksplisit.

| `type` | Protokol | Catatan |
|---|---|---|
| `anthropic` | Messages API (SSE) | Breakpoint prompt-cache eksplisit, extended thinking dengan round-trip tanda tangan, vision, PDF, penghitung token. Mendukung gateway kompatibel Anthropic lewat `baseUrl` (mis. `/anthropic` milik DeepSeek), `useBearerAuth`, `betas`. |
| `openai` | Responses API (default) atau Chat Completions (`"api": "chat"`) | Stateless (`store:false`) — transkrip tetap milik DotCode; item reasoning terenkripsi dikirim balik; reasoning effort. |
| `azure` | Sama dengan `openai`, header `api-key` | `baseUrl: https://<resource>.openai.azure.com/openai/v1`; model = nama deployment. |
| `gemini` | `streamGenerateContent` (SSE) | Skema tool disesuaikan ke subset OpenAPI Gemini; *thought signature* dipertahankan; budget thinking. |
| `deepseek` | Chat Completions | `reasoning_content` ditampilkan sebagai thinking dan dikirim balik selama loop tool; statistik cache hit. |
| `ollama` | `/api/chat` native (NDJSON) | Memeriksa `/api/show` (tools/vision/thinking, panjang konteks); **menaikkan `num_ctx` otomatis** (default Ollama yang kecil diam-diam memotong prompt agen). |
| `openai-compatible` | Chat Completions | LM Studio, vLLM, LiteLLM, OpenRouter, Groq, Together, Fireworks… diatur dengan `profile` dan `quirks` — tanpa ubah kode. |
| `mock` | Skrip offline | Respons deterministik dari file JSON (test, demo, konformansi SDK). |

## Contoh

```jsonc
{
  "providers": {
    "anthropic":  { "type": "anthropic", "apiKey": "${env:ANTHROPIC_API_KEY}" },
    "openai":     { "type": "openai", "apiKey": "${env:OPENAI_API_KEY}" },
    "azure":      { "type": "azure", "baseUrl": "https://myres.openai.azure.com/openai/v1", "apiKey": "${env:AZURE_OPENAI_API_KEY}", "models": ["gpt-5-mini"] },
    "gemini":     { "type": "gemini", "apiKey": "${env:GEMINI_API_KEY}" },
    "deepseek":   { "type": "deepseek", "apiKey": "${env:DEEPSEEK_API_KEY}" },
    "lokal":      { "type": "ollama", "baseUrl": "http://localhost:11434", "numCtx": 32768 },
    "openrouter": { "type": "openai-compatible", "baseUrl": "https://openrouter.ai/api/v1", "apiKey": "${env:OPENROUTER_API_KEY}", "profile": "openrouter" },
    "internal":   { "type": "openai-compatible", "baseUrl": "https://llm.internal/v1", "apiKey": "${env:INTERNAL_KEY}",
                    "quirks": { "roleForSystem": "system", "supportsStreamUsage": false, "maxTokensParam": "max_tokens" } }
  }
}
```

### Profil quirk

`openai`, `azure`, `deepseek`, `openrouter`, `vllm`/`sglang`, `lmstudio`/`ollama`/`llamacpp`, `groq`/`together`/`fireworks`/`litellm`/`mistral`. Quirk individual menimpa profil: `roleForSystem`, `maxTokensParam`, `supportsStreamUsage`, `parallelTools`, `reasoningField`, `sendReasoningBack`, `supportsTemperature`, `supportsReasoningEffort`, `authHeader`.

### Override kapabilitas dan harga

Kapabilitas (jendela konteks, output maksimum, vision, gaya reasoning, caching, dialek skema) dan harga USD berasal dari katalog bawaan berdasarkan prefix id model. Override per model dengan `modelOverrides`.

## Degradasi anggun

| Kapabilitas yang tidak ada | Yang dilakukan DotCode |
|---|---|
| Function calling native | **Text tool protocol**: definisi tool masuk ke system prompt dan blok `<tool_call>{…}</tool_call>` diurai dengan toleran. |
| Vision | Gambar diganti penanda. |
| Kontrol reasoning | `effort` diabaikan untuk model itu. |
| Endpoint hitung token | Estimasi lokal + usage dari provider. |
| Argumen tool rusak saat streaming | Perbaikan JSON otomatis; jika gagal model menerima error validasi dan mencoba lagi. |

## Retry, fallback, dan biaya

Error sementara di-retry dengan exponential backoff (hingga 8 kali, menghormati `Retry-After`), lalu rantai `fallback` (atau `--fallback-model`) mengambil alih. "Konteks terlalu panjang" memicu kompaksi otomatis. `/cost` dan ringkasan saat keluar menampilkan token dan estimasi USD per model.

## Rekam dan putar ulang

`--record skrip.json` merekam setiap respons model dalam format provider `mock`; putar ulang offline dengan `{"type":"mock","script":"skrip.json"}`.
