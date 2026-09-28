# Provider LLM

> 🇬🇧 [English](../en/providers.md)

DotCode menormalkan semua provider ke satu model pesan (teks, gambar, PDF, pemanggilan tool, hasil tool, reasoning dengan tanda tangan milik provider) dan satu model event streaming. Setiap adapter menerjemahkan — dan bila perlu mendegradasi — secara eksplisit.

| `type` | Protokol | Catatan |
|---|---|---|
| `anthropic` | Messages API (SSE) | Breakpoint prompt-cache eksplisit, extended thinking dengan round-trip tanda tangan, vision, PDF, penghitung token. Mendukung gateway kompatibel Anthropic lewat `baseUrl` (mis. `/anthropic` milik DeepSeek), `useBearerAuth`, `betas`. |
| `openai` | Responses API (default) atau Chat Completions (`"api": "chat"`) | Stateless (`store:false`) — transkrip tetap milik DotCode; item reasoning terenkripsi dikirim balik; reasoning effort. |
| `bedrock` | Anthropic Messages di Amazon Bedrock (AWS event stream) | SigV4 dengan kredensial AWS (env, profil `~/.aws`, `credential_process`, SSO lewat AWS CLI) atau API key Bedrock. Lihat [Platform cloud](#platform-cloud). |
| `vertex` | Anthropic Messages di Google Vertex AI (SSE) | OAuth dari Application Default Credentials. Lihat [Platform cloud](#platform-cloud). |
| `azure` | Sama dengan `openai`, header `api-key` | `baseUrl: https://<resource>.openai.azure.com/openai/v1`; model = nama deployment. Atau `"auth": "entra"` untuk token Microsoft Entra ID sebagai ganti key. |
| `gemini` | `streamGenerateContent` (SSE) | Skema tool disesuaikan ke subset OpenAPI Gemini; *thought signature* dipertahankan; budget thinking. `"platform": "vertex"` (atau `type: vertex-gemini`) memanggil Gemini lewat Vertex AI dengan OAuth. |
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

## Platform cloud

### Amazon Bedrock (Claude)

```jsonc
"providers": { "bedrock": { "type": "bedrock", "region": "us-east-1", "awsProfile": "work" } }   // model: bedrock:sonnet
```

- **Kredensial**, yang pertama cocok dipakai:
  1. `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` (+ `AWS_SESSION_TOKEN`);
  2. `awsProfile` (atau `AWS_PROFILE`) di `~/.aws/credentials` atau `~/.aws/config`, termasuk `credential_process`;
  3. `aws configure export-credentials`, yang mencakup SSO (`aws sso login`) dan profil assumed-role.

  Request ditandatangani dengan SigV4. **API key Bedrock** (`apiKey` atau `AWS_BEARER_TOKEN_BEDROCK`) dikirim sebagai bearer token sebagai gantinya.
- **Region:** `region`, `AWS_REGION` / `AWS_DEFAULT_REGION`, region milik profil, selain itu `us-east-1`.
- **Model:** gunakan id model atau inference profile Bedrock, mis. `us.anthropic.claude-sonnet-4-5-20250929-v1:0`. `bedrock:sonnet`, `bedrock:opus`, dan `bedrock:haiku` dipetakan ke inference profile US terkini. Alias polos `sonnet` / `opus` / `haiku` otomatis memakai Bedrock bila tidak ada provider Anthropic langsung. `betas` dikirim sebagai `anthropic_beta`.
- **Kompatibel Claude Code:** `CLAUDE_CODE_USE_BEDROCK=1` (atau `DOTCODE_USE_BEDROCK=1`) menambahkan provider secara otomatis. `ANTHROPIC_MODEL` mengatur model default, dan `ANTHROPIC_BEDROCK_BASE_URL` mengganti endpoint.
- **Error:** exception dari event stream dipetakan ke logika retry DotCode. `throttlingException` di-retry seperti rate limit.

### Google Vertex AI (Claude dan Gemini)

```jsonc
"providers": {
  "vertex":        { "type": "vertex", "project": "proyek-saya", "region": "us-east5" },        // Claude: vertex:claude-sonnet-4-5@20250929 atau vertex:sonnet
  "gemini-vertex": { "type": "gemini", "platform": "vertex", "project": "proyek-saya", "region": "global" }   // Gemini: gemini-vertex:gemini-2.5-pro
}
```

- **Kredensial** (Application Default Credentials), yang pertama cocok dipakai:
  1. `GOOGLE_OAUTH_ACCESS_TOKEN`;
  2. `credentialsFile` / `GOOGLE_APPLICATION_CREDENTIALS`: key service account ditukar dengan JWT RS256 bertanda tangan, sedangkan file authorized-user dari `gcloud auth application-default login` memakai refresh token-nya;
  3. metadata server di GCE, GKE, atau Cloud Run;
  4. `gcloud auth print-access-token`.

  Token di-cache dan diperbarui sebelum kedaluwarsa.
- **Project:** `project`, `ANTHROPIC_VERTEX_PROJECT_ID`, `GOOGLE_CLOUD_PROJECT`, atau project milik service account.
- **Lokasi:** `region`, `CLOUD_ML_REGION`, `GOOGLE_CLOUD_LOCATION` (default Claude `us-east5`, Gemini `global`).
- **Kompatibel Claude Code:** `CLAUDE_CODE_USE_VERTEX=1` (atau `DOTCODE_USE_VERTEX=1`) bersama `ANTHROPIC_VERTEX_PROJECT_ID` dan `CLOUD_ML_REGION`.

### Azure OpenAI dengan Microsoft Entra ID

```jsonc
"providers": { "azure": { "type": "azure", "baseUrl": "https://myres.openai.azure.com/openai/v1", "auth": "entra", "models": ["gpt-5-mini"] } }
```

- **Kredensial** (seperti `DefaultAzureCredential`), yang pertama cocok dipakai:
  1. service principal (`AZURE_TENANT_ID` + `AZURE_CLIENT_ID` + `AZURE_CLIENT_SECRET`, atau `tenantId` / `clientId` / `clientSecret` di pengaturan);
  2. workload identity (`AZURE_FEDERATED_TOKEN_FILE`);
  3. managed identity (endpoint App Service / Container Apps, atau IMDS milik VM);
  4. Azure CLI (`az login`).
- **Token:** diminta untuk `https://cognitiveservices.azure.com/.default` (bisa diganti lewat `scope`), di-cache, dan dikirim sebagai bearer token sebagai ganti `api-key`.
- **Dari variabel lingkungan:** bila `AZURE_OPENAI_ENDPOINT` diatur tanpa `AZURE_OPENAI_API_KEY`, mengatur `AZURE_OPENAI_USE_ENTRA=1` atau `AZURE_CLIENT_ID` akan mengonfigurasi ini secara otomatis.

> Integrasi ini mengikuti spesifikasi publik AWS SigV4 / event-stream, Google OAuth, dan Microsoft identity. Integrasi ini dicakup oleh test (test vector resmi SigV4, verifikasi tanda tangan JWT, serta endpoint token dan format wire yang di-mock). Integrasi ini belum diuji terhadap akun AWS, Google Cloud, atau Azure sungguhan; mohon laporkan bila menemukan masalah.

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
