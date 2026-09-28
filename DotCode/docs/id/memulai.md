# Memulai

> 🇬🇧 [Read in English](../en/getting-started.md)

DotCode adalah alat *agentic coding* di terminal — implementasi ulang pengalaman Claude Code di atas .NET 10 yang bekerja dengan **LLM apa pun** (Anthropic, OpenAI, Azure OpenAI, Google Gemini, DeepSeek, Ollama, dan server kompatibel OpenAI), lengkap dengan **SDK harness** untuk .NET, TypeScript, Python, Go, dan Java.

*Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil.*

## 1. Instalasi

| Opsi | Perintah |
|---|---|
| Binary native (disarankan) | Unduh `dotcode` untuk OS Anda dari halaman *releases*, letakkan di `PATH` |
| .NET global tool | `dotnet tool install -g DotCode.Cli` (butuh runtime .NET 10) |
| Dari source | `git clone … && cd DotCode && dotnet publish src/DotCode.Cli -c Release -r <rid> -o out` |

`<rid>`: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, atau `osx-arm64`. Hasilnya satu executable NativeAOT (~14 MB) tanpa prasyarat runtime.

Periksa instalasi:

```bash
dotcode --version
dotcode doctor
```

Opsional: **Git** (Git Bash di Windows dan fitur yang sadar git) serta **ripgrep** (`rg`, pencarian lebih cepat; tersedia fallback bawaan).

## 2. Konfigurasi provider model

Cara tercepat: variabel lingkungan yang dideteksi otomatis:

| Provider | Variabel lingkungan |
|---|---|
| Anthropic | `ANTHROPIC_API_KEY` (opsional `ANTHROPIC_BASE_URL`) |
| OpenAI | `OPENAI_API_KEY` (opsional `OPENAI_BASE_URL`) |
| Azure OpenAI | `AZURE_OPENAI_API_KEY` + `AZURE_OPENAI_ENDPOINT` |
| Google Gemini | `GEMINI_API_KEY` (atau `GOOGLE_API_KEY`) |
| DeepSeek | `DEEPSEEK_API_KEY` |
| Ollama | `OLLAMA_HOST` (mis. `http://localhost:11434`) |

Untuk kendali penuh gunakan `~/.dotcode/settings.json` (lihat [Konfigurasi](konfigurasi.md)):

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

Simpan rahasia di variabel lingkungan dan rujuk dengan `${env:NAMA}` — jangan pernah menempelkan kunci API ke file yang di-commit.

## 3. Mulai sesi

```bash
cd proyek-anda
dotcode                              # sesi interaktif
dotcode "jelaskan codebase ini"       # langsung dengan prompt
dotcode --model deepseek:deepseek-v4-flash
```

![Layar sambutan](../images/welcome.png)

Yang bisa dicoba pertama kali:

- `/init` — membuat `DOTCODE.md` berisi perintah build dan catatan arsitektur repo
- `jelaskan arsitektur proyek ini`
- `tambahkan validasi input di <file> dan tulis unit test-nya`
- `!dotnet test` — menjalankan perintah shell sendiri (mode bash)
- `@src/Program.cs apa fungsi file ini?` — melampirkan file
- **Shift+Tab** — berganti mode *default → accept edits → plan*
- `/model`, `/theme`, `/help`

DotCode menjawab dalam bahasa Anda — prompt berbahasa Indonesia dijawab dalam Bahasa Indonesia:

![Landing page dibuat dari prompt berbahasa Indonesia](../images/web-app-done.png)

## 4. Penggunaan non-interaktif

```bash
dotcode -p "ringkas git log minggu lalu"
git diff | dotcode -p "review diff ini" --output-format json
dotcode -p "perbaiki test yang gagal" --permission-mode acceptEdits --allowedTools "Bash(dotnet test*)"
```

Lihat [Mode headless](headless.md).

## 5. Sematkan ke aplikasi Anda

```python
from dotcode_sdk import DotCodeClient
async with DotCodeClient() as client:
    session = await client.create_session(model="openai:gpt-5")
    print((await session.send("Daftar TODO di repo ini"))["result"])
```

SDK tersedia untuk .NET, TypeScript, Python, Go, dan Java — lihat [SDK](sdk.md).

## Langkah berikutnya

- [Mode interaktif](mode-interaktif.md) — UI, pintasan, slash command, tema
- [Izin](izin.md) — mode, aturan, `--dangerously-skip-permissions`
- [Tools](tools.md) — apa saja yang bisa dilakukan agen
- [Ekstensi](ekstensi.md) — skills, commands, subagent, hooks, plugin, MCP
- [Provider](provider.md) — semua LLM yang didukung
- [Arsitektur](arsitektur.md) — cara DotCode dibangun
