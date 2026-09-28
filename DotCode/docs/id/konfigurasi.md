# Konfigurasi

> 🇬🇧 [English](../en/configuration.md)

## File pengaturan dan prioritas

Pengaturan berformat JSON (boleh komentar dan koma di akhir) dan **digabung secara mendalam** dari prioritas terendah ke tertinggi:

| # | Cakupan | Path |
|---|---|---|
| 1 | Pengguna | `~/.dotcode/settings.json` |
| 2 | Proyek (dibagikan) | `.claude/settings.json` (kompatibilitas), `.dotcode/settings.json` |
| 3 | Lokal (tidak di-commit) | `.claude/settings.local.json` (kompatibilitas), `.dotcode/settings.local.json` |
| 4 | CLI | `--settings <file-atau-json-inline>` |
| 5 | Terkelola (kebijakan organisasi) | Windows `%ProgramData%\DotCode\managed-settings.json`, macOS `/Library/Application Support/DotCode/`, Linux `/etc/dotcode/` |

Objek digabung rekursif, nilai skalar ditimpa, daftar izin (`allow`, `deny`, `ask`, `additionalDirectories`) disambung. `DOTCODE_CONFIG_DIR` memindahkan direktori pengguna. Lihat hasil gabungan dengan `dotcode config list`; ubah dengan `dotcode config set [-g] <kunci> <nilai>`.

Untuk migrasi, DotCode juga membaca `.claude/settings*.json` proyek dari Claude Code (kunci khusus Claude seperti `model` diabaikan), `CLAUDE.md`, `.claude/skills|commands|agents` dan `~/.claude/skills|commands|agents`.

## Referensi

```jsonc
{
  // Model utama: provider:model, alias (sonnet, opus, haiku, gpt, gemini, deepseek) atau peran
  "model": "anthropic:claude-sonnet-4-5",

  // Model per peran. "fast" untuk judul sesi, ringkasan WebFetch, subagent Explore;
  // "planner" untuk subagent Plan; "subagent" untuk subagent umum.
  "models": { "main": "...", "fast": "...", "planner": "...", "subagent": "...", "advisor": "..." },

  // Instans provider bernama (nama = prefix pada "nama:model")
  "providers": { "azure-kantor": { "type": "azure", "baseUrl": "...", "apiKey": "${env:AZURE_KEY}" } },

  // Rantai fallback saat error sementara (setelah retry); pindah model di batas turn yang aman
  "fallback": [ { "on": ["rate_limit", "overloaded", "5xx"], "chain": ["openai:gpt-5", "deepseek:deepseek-chat"] } ],

  "permissions": {
    "allow": ["Bash(npm run test:*)", "Read(~/catatan/**)", "WebFetch(domain:learn.microsoft.com)"],
    "ask":   ["Bash(git push:*)"],
    "deny":  ["Read(./.env)", "Bash(rm -rf *)"],
    "defaultMode": "default",               // default | acceptEdits | auto | plan | bypassPermissions
    "autoMode": { "enabled": true, "model": "openai:gpt-5-mini", "guidance": "..." },
    "additionalDirectories": ["../shared-lib"],
    "disableBypassPermissionsMode": false   // true di managed settings untuk melarang bypass
  },

  "hooks": { "PostToolUse": [ { "matcher": "Edit|Write", "hooks": [ { "type": "command", "command": "dotnet format" } ] } ] },
  "env": { "DOTNET_CLI_TELEMETRY_OPTOUT": "1" },
  "mcpServers": { "github": { "type": "http", "url": "https://api.githubcopilot.com/mcp/", "headers": { "Authorization": "Bearer ${env:GH_TOKEN}" } } },
  "enabledPlugins": { "gravicode-toolkit@gravicode": true },

  "theme": "dark",
  "tui": {
    "glyphs": "unicode",                     // unicode | ascii | nerd  (sesuaikan dengan font terminal)
    "border": "lines",                       // lines | rounded | single | double | heavy (gaya kotak input)
    "spinner": "claude",                     // claude | dots | line | star | bounce | arc | dotnet
    "accent": "#D77757",                     // ganti warna aksen tema
    "reducedMotion": false,
    "showThinking": false,
    "showTips": true
  },
  "spinnerVerbs": ["Menyeduh", "Mengompilasi"],
  "statusLine": { "type": "command", "command": "~/.dotcode/statusline.sh" },

  "outputStyle": "default",                  // default | explanatory | learning | nama kustom
  "effort": "medium",                        // off | low | medium | high | xhigh
  "maxOutputTokens": 32000,
  "maxTurns": 100,
  "autoCompact": true,
  "autoCompactThreshold": 0.85,
  "promptCaching": true,
  "budget": { "maxUsdPerSession": 5, "action": "warn" },
  "allowedProviders": ["ollama"],            // kebijakan: hanya provider ini yang boleh dipakai
  "webSearch": { "provider": "tavily", "apiKey": "${env:TAVILY_API_KEY}" }
}
```

## Rujukan model

| Bentuk | Contoh | Arti |
|---|---|---|
| `provider:model` | `deepseek:deepseek-v4-flash` | Instans provider dan model eksplisit |
| Model saja | `gpt-5-mini` | Model pada provider pertama yang terkonfigurasi |
| Alias | `sonnet`, `opus`, `haiku`, `gpt`, `gemini`, `deepseek` | Default yang wajar pada provider bertipe tersebut |
| Peran | `fast`, `planner`, `subagent` | Diselesaikan lewat `models.<peran>` (fallback ke main) |
| Tag Ollama | `ollama:qwen3:8b` | Hanya titik dua pertama yang memisahkan provider |

## File memori (DOTCODE.md)

Dimuat ke setiap percakapan, berurutan: `DOTCODE.md` terkelola; milik pengguna `~/.dotcode/DOTCODE.md` dan `~/.claude/CLAUDE.md`; lalu untuk setiap direktori dari root proyek sampai direktori kerja: `DOTCODE.md`, `.dotcode/DOTCODE.md`, `CLAUDE.md`, `.claude/CLAUDE.md`, `AGENTS.md`, dan file pribadi `DOTCODE.local.md` / `CLAUDE.local.md`. File bisa mengimpor file lain dengan `@path/relatif.md`. Tambah memori cepat dengan mengawali prompt dengan `#`, atau `/memory`.

## File dan data

| Path | Isi |
|---|---|
| `~/.dotcode/projects/<proyek>/<sesi>.jsonl` | Transkrip sesi (`--continue` / `--resume`) |
| `~/.dotcode/projects/<proyek>/checkpoints/` | Snapshot file untuk `/rewind` |
| `~/.dotcode/history.jsonl` | Riwayat prompt (↑/↓) |
| `~/.dotcode/mcp.json` | Server MCP cakupan pengguna |
| `.mcp.json` | Server MCP cakupan proyek |
| `~/.dotcode/plugins/` | Plugin dan marketplace terpasang |
| `~/.dotcode/themes/*.json` | Tema kustom |
