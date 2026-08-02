# Hooks

> Auto Code — Gravicode Studios, dipimpin oleh Kang Fadhil

Hook adalah perintah shell yang terikat pada sebuah peristiwa siklus hidup. Hook memungkinkan Anda
menegakkan kebijakan dan mengotomatiskan hal di sekitar agent secara deterministik — harness yang
menjalankannya, sehingga hook tetap berjalan terlepas dari kerja sama model.

## Peristiwa

| Peristiwa | Terjadi saat | Bisa memblokir |
| --- | --- | --- |
| `SessionStart` | Sesi dimulai | tidak |
| `UserPromptSubmit` | Sebelum prompt sampai ke model | ya |
| `PreToolUse` | Sebelum tool berjalan, sebelum permintaan izin | **ya** |
| `PostToolUse` | Setelah tool selesai | tidak |
| `PreCompact` | Sebelum pemadatan konteks | tidak |
| `SubagentStop` | Subagent selesai | tidak |
| `Stop` | Giliran berakhir | tidak |

## Konfigurasi

```jsonc
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write|MultiEdit",
        "command": "dotnet format --include $(git diff --name-only --diff-filter=ACM)",
        "timeoutMs": 60000,
        "blocking": false
      }
    ],
    "PreToolUse": [
      {
        "matcher": "Bash",
        "command": "pwsh -File .autocode/guard-commands.ps1",
        "blocking": true
      }
    ]
  }
}
```

| Kolom | Arti |
| --- | --- |
| `matcher` | Glob terhadap nama tool. Kosongkan untuk mencocokkan semuanya. |
| `command` | Perintah shell. Menerima payload peristiwa sebagai JSON via stdin. |
| `timeoutMs` | Bawaan 30.000. Bila lewat batas, hook diabaikan, tidak pernah fatal. |
| `blocking` | Untuk peristiwa `Pre*`: exit code bukan nol menolak aksi. Bawaan `true`. |

## Kontraknya

**Masukan** — sebuah objek JSON melalui stdin:

```json
{
  "hook_event_name": "PreToolUse",
  "session_id": "a1b2c3d4",
  "tool_name": "Bash",
  "tool_input": { "command": "git push --force origin main" }
}
```

Tersedia juga sebagai environment variable: `AUTOCODE_HOOK_EVENT`, `AUTOCODE_WORKSPACE`.

**Keluaran**

- **Exit code 0** — izinkan. Apa pun di stdout diteruskan ke model sebagai konteks tambahan.
- **Bukan nol** pada peristiwa `Pre*` yang blocking — tolak. stderr (atau stdout) menjadi alasan yang
  disampaikan ke model, sehingga ia bisa menyesuaikan diri alih-alih mencoba ulang secara buta.

Perilaku stdout-sebagai-konteks itulah yang membuat sebuah hook bisa menjelaskan dirinya:

```bash
#!/usr/bin/env bash
# PostToolUse pada Edit — beri tahu agent ketika ia merusak build
if ! dotnet build --nologo -v q > /tmp/build.log 2>&1; then
  echo "Build sekarang gagal:"
  tail -20 /tmp/build.log
fi
exit 0
```

## Hook yang berguna

**Format setelah setiap suntingan**

```jsonc
{ "hooks": { "PostToolUse": [
  { "matcher": "Edit|Write|MultiEdit", "command": "dotnet format --no-restore", "blocking": false }
]}}
```

**Blokir force push apa pun mode izinnya**

```powershell
# .autocode/guard-commands.ps1
$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
$command = $payload.tool_input.command

if ($command -match 'push\s+.*--force' -and -not ($command -match '--force-with-lease')) {
    Write-Error "Force push tidak diizinkan di sini. Gunakan --force-with-lease."
    exit 1
}
exit 0
```

**Lindungi branch rilis**

```bash
#!/usr/bin/env bash
branch=$(git rev-parse --abbrev-ref HEAD)
if [[ "$branch" == release/* ]]; then
  echo "Menyunting langsung di $branch tidak diizinkan. Kerjakan di feature branch." >&2
  exit 1
fi
exit 0
```

**Beri notifikasi saat proses panjang selesai**

```jsonc
{ "hooks": { "Stop": [
  { "command": "notify-send 'Auto Code' 'Giliran selesai'", "blocking": false }
]}}
```

## Hook versus aturan izin

Keduanya menolak aksi. Keduanya menjawab pertanyaan yang berbeda.

- **Aturan izin** adalah fakta statis tentang argumennya: *jangan pernah baca `.env`*. Murah,
  deklaratif, tanpa proses baru.
- **Hook** adalah keputusan yang bergantung pada keadaan yang tidak terkandung di argumen: branch
  saat ini, kebersihan working tree, waktu, atau layanan kebijakan eksternal.

Pakai aturan lebih dulu. Pakai hook ketika aturan tidak mampu menyatakan kondisinya.

## Catatan

- Hook berjalan di akar workspace.
- Hook yang gagal dijalankan dilaporkan lalu diabaikan — hook rusak tidak boleh mengorbankan sesi.
- Hook yang melewati batas waktu diabaikan, tidak pernah dianggap sebagai blokir.
- `PreToolUse` berjalan **sebelum** permintaan izin, sehingga penolakan oleh hook berarti pengguna
  tidak pernah ditanya.
- Plugin bisa menyumbang hook; hook plugin digabungkan dengan milik Anda, bukan menggantikannya.
