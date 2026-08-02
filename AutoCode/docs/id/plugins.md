# Plugin

> Auto Code — Gravicode Studios, dipimpin oleh Kang Fadhil

Plugin adalah direktori yang membungkus skill, subagent, hook, dan deklarasi server MCP sehingga
sebuah tim bisa membagikan seluruh setup kerjanya sebagai satu kesatuan.

Tidak ada isi plugin yang dikompilasi atau dieksekusi saat pemuatan. Plugin menyumbang **deklarasi**;
hook-nya baru berjalan ketika peristiwanya terjadi, dan server MCP-nya tunduk pada mesin izin yang
sama dengan semua hal lain.

## Struktur

```
my-plugin/
├── autocode-plugin.json      # manifest wajib
├── skills/
│   ├── deploy/SKILL.md
│   └── rollback/SKILL.md
└── agents/
    ├── security-reviewer.md
    └── perf-analyst.md
```

```jsonc
// autocode-plugin.json
{
  "name": "acme-platform",
  "version": "1.2.0",
  "description": "Alur kerja deployment dan peninjau untuk platform ACME",

  "hooks": {
    "PostToolUse": [
      { "matcher": "Edit|Write", "command": "./scripts/check-conventions.sh", "blocking": false }
    ]
  },

  "mcpServers": {
    "acme-deploy": { "command": "npx", "args": ["-y", "@acme/mcp-deploy"] }
  }
}
```

Folder `skills/` dan `agents/` memakai format yang persis sama dengan milik workspace — lihat
[skills](skills.md) dan [subagent](subagents.md).

## Di mana plugin dicari

1. `~/.autocode/plugins/` — milik Anda, semua proyek
2. `<workspace>/.autocode/plugins/` — milik proyek
3. Apa pun yang tercantum di `plugins` pada settings

```jsonc
{ "plugins": ["tools/autocode-plugins/acme-platform", "../shared/autocode-plugins"] }
```

Jalur yang dikonfigurasi boleh berupa satu plugin (berisi `autocode-plugin.json`) atau direktori
berisi banyak plugin.

## Prioritas

Skill dan agent dari plugin digabungkan setelah milik workspace, sehingga plugin bisa menambah apa
yang sudah didefinisikan proyek. Skill workspace dan skill plugin dengan nama sama akan menghasilkan
milik plugin — yang berarti memberi nama khas pada kontribusi plugin adalah langkah yang berguna.

Hook plugin **ditambahkan** ke milik Anda, bukan menggantikannya. Server MCP plugin ditambahkan hanya
bila namanya belum dipakai, sehingga sebuah workspace bisa menimpanya secara sengaja.

## Distribusi

Plugin hanyalah direktori biasa, jadi mekanisme apa pun bisa dipakai:

```bash
# Git submodule
git submodule add https://github.com/acme/autocode-plugin tools/autocode-plugins/acme

# Atau cukup clone ke direktori pengguna
git clone https://github.com/acme/autocode-plugin ~/.autocode/plugins/acme
```

Lalu rujuk jalurnya — atau, di direktori pengguna, tidak perlu apa-apa lagi, karena lokasi itu
dipindai otomatis.

## Memastikan yang termuat

```
› /skills     # skill, termasuk dari plugin
› /agents     # subagent
› /mcp        # server yang dideklarasikan plugin
```

Plugin dengan manifest tidak valid akan dilewati diam-diam alih-alih menggagalkan sesi. Bila ada yang
Anda harapkan tetapi tidak muncul di daftar itu, periksa apakah JSON-nya bisa diurai.

## Yang sengaja bukan menjadi plugin

Tidak ada API plugin terkompilasi — tidak ada pemuatan assembly, tidak ada antarmuka `IPlugin` untuk
diimplementasikan. Memuat assembly .NET sembarangan ke dalam proses agent berarti sebuah plugin bisa
melewati mesin izin sepenuhnya, dan itulah satu batas yang layak dijaga mutlak.

Bila Anda memerlukan kemampuan yang benar-benar baru, bukan sekadar konfigurasi baru, buatlah
[server MCP](mcp.md). Ia berjalan di prosesnya sendiri, bekerja dengan semua klien MCP, dan bisa
ditulis dengan bahasa apa pun.
