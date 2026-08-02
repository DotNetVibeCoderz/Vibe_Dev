# Konfigurasi

> Auto Code — Gravicode Studios, dipimpin oleh Kang Fadhil

## Dari mana pengaturan berasal

Sumber pengaturan berlapis. Lapisan berikutnya menimpa yang sebelumnya.

| # | Sumber | Kegunaan |
| --- | --- | --- |
| 1 | Preset vendor bawaan | Nilai bawaan yang masuk akal untuk provider yang dikenal |
| 2 | `<appSettings>` pada `app.config` / `autocode.dll.config` | Deployment .NET klasik |
| 3 | `~/.autocode/settings.json` | Preferensi Anda, berlaku di semua proyek |
| 4 | `<workspace>/.autocode/settings.json` | Pengaturan proyek, ikut di-commit |
| 5 | `<workspace>/.autocode/settings.local.json` | Penyesuaian Anda, masuk gitignore |
| 6 | Environment variable `AUTOCODE_*` | CI dan kontainer |
| 7 | Argumen baris perintah | Hanya untuk sekali jalan ini |

Konsekuensi praktisnya: commit lapisan 4, simpan rahasia di lapisan 5 atau 6.

## Menemukan akar workspace

Auto Code menelusuri ke atas dari direktori saat ini sampai menemukan `.git/`, `.autocode/`,
`AUTOCODE.md`, atau `CLAUDE.md`. Direktori itu menjadi akar workspace: settings, sesi, skill, dan
agent semuanya dihitung relatif terhadapnya. Ubah dengan `--cwd`.

## Berkas settings

```jsonc
{
  // Profil provider yang dipakai. Bawaannya profil pertama yang punya API key valid.
  "activeProvider": "openai",

  "providers": {
    "openai": {
      "kind": "OpenAICompatible",           // OpenAICompatible | Anthropic | Gemini
      "endpoint": "https://api.openai.com/v1",
      "apiKey": "env:OPENAI_API_KEY",       // "env:NAMA" dibaca saat dipakai
      "model": "gpt-4.1",
      "smallModel": "gpt-4.1-mini",         // dipakai untuk ringkasan dan kerja latar
      "embeddingModel": "text-embedding-3-small",
      "embeddingDimensions": 1536,
      "temperature": 0.0,
      "maxOutputTokens": 8192,
      "contextWindow": 1000000,             // menentukan ambang pemadatan otomatis
      "enableExtendedThinking": false,
      "thinkingBudgetTokens": 8000,
      "supportsParallelToolCalls": true,    // set false untuk sebagian runtime lokal
      "inputCostPerMillionTokens": 2.0,     // 0 mematikan tampilan biaya
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

  "maxTurnIterations": 100,                 // batas putaran pemanggilan tool per pesan pengguna
  "compactionThreshold": 0.82,              // porsi jendela konteks yang memicu pemadatan
  "persistSessions": true,
  "showCost": true,
  "showThinking": true,
  "language": "en",                         // en | id — bahasa antarmuka dan jawaban

  "verifyCommands": ["dotnet build", "dotnet test"],
  "contextFiles": ["docs/konvensi.md"],
  "skillDirectories": ["tools/skills"],
  "plugins": ["tools/autocode-plugins"],
  "disabledTools": ["WebFetch"],

  "shell": "pwsh",                          // bawaan: pwsh/powershell di Windows, $SHELL di lainnya
  "bashTimeoutMs": 120000,

  "enableSemanticIndex": false,
  "embeddings": {                           // dikonfigurasi terpisah dari chat — lihat providers.md
    "kind": "Auto",                         // Auto | Ollama | Onnx | OpenAICompatible | None
    "model": "nomic-embed-text",            // Ollama / OpenAI-compatible
    "endpoint": "http://localhost:11434",   // bawaan Ollama
    "modelPath": "models/model.onnx",       // khusus Onnx
    "vocabPath": "models/vocab.txt",        // khusus Onnx
    "maxTokens": 512,                       // khusus Onnx
    "lowerCase": true,                      // khusus Onnx — false untuk model -cased
    "batchSize": 64,
    "dimensions": 0                         // 0 = simpulkan dari batch pertama. Biarkan 0.
  },

  "agents": {},
  "teams": {},
  "hooks": {},
  "mcpServers": {}
}
```

Semua kunci bersifat opsional. `{}` kosong tetap sah dan akan memakai preset.

## Rahasia

Jangan pernah menaruh key aktif di berkas yang ikut di-commit. Pakai salah satu cara ini:

```jsonc
// .autocode/settings.json — ikut di-commit
{ "providers": { "openai": { "apiKey": "env:OPENAI_API_KEY" } } }
```

```jsonc
// .autocode/settings.local.json — masuk gitignore
{ "providers": { "openai": { "apiKey": "sk-key-asli" } } }
```

```bash
export AUTOCODE_PROVIDERS__OPENAI__APIKEY=sk-key-asli
```

`env:NAMA` dibaca setiap kali key diperlukan, bukan sekali saat pemuatan — sehingga rotasi variabel
langsung berlaku tanpa perlu memulai ulang.

## Environment variable

Aturan binding konfigurasi .NET berlaku: `__` adalah pemisah bagian.

```bash
AUTOCODE_ACTIVEPROVIDER=deepseek
AUTOCODE_PERMISSIONMODE=acceptEdits
AUTOCODE_PROVIDERS__DEEPSEEK__MODEL=deepseek-reasoner
```

Ada dua jalan pintas karena keduanya paling sering dipakai:

```bash
AUTOCODE_PROVIDER=ollama      # sama dengan --provider
AUTOCODE_MODEL=qwen2.5-coder  # sama dengan --model
```

## app.config

Untuk lingkungan yang mengelola aplikasi .NET lewat `app.config`, kunci di bawah `<appSettings>`
dibaca memakai jalur konfigurasi yang sama. Pemisah `:` maupun `.` sama-sama berlaku.

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

Ini adalah lapisan dengan prioritas **terendah** setelah preset, sehingga `settings.json` maupun
environment variable tetap menang.

## Memeriksa yang sedang berlaku

```bash
autocode config show     # pengaturan hasil resolusi dan asalnya
autocode config path     # berkas yang sedang dibaca
autocode config init     # buat kerangka berkas settings proyek
autocode doctor          # verifikasi seluruh jalur, termasuk permintaan sungguhan
```

Di dalam sesi, `/status` menampilkan hal yang sama ditambah kondisi sesi yang sedang berjalan.
