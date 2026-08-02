# Izin

> Auto Code — Gravicode Studios, dipimpin oleh Kang Fadhil

## Prinsipnya

Agent yang bisa menyunting berkas dan menjalankan perintah memerlukan batas. Batas Auto Code adalah:
**bebas membaca, bertanya sebelum mengubah apa pun**.

Tool yang hanya membaca — `Read`, `Grep`, `Glob`, `List` — tidak pernah bertanya, di mode mana pun.
Semua yang menulis, mengeksekusi, atau menyentuh jaringan melewati mesin izin.

## Mode

| Mode | Penulisan | Perintah | Jaringan |
| --- | --- | --- | --- |
| `ask` *(bawaan)* | bertanya | bertanya | bertanya |
| `acceptEdits` | diizinkan | bertanya | bertanya |
| `plan` | **ditolak** | **ditolak** | bertanya |
| `bypassPermissions` | diizinkan | diizinkan | diizinkan |

```bash
autocode --permission-mode plan "bagaimana cara menambahkan rate limiting?"
```

```
› /permissions acceptEdits
```

`plan` layak dipahami betul. Ia bukan "lebih sering bertanya" — perubahan ditolak keras, sehingga
agent tidak bisa mencoba satu perubahan untuk melihat apakah diizinkan. Yang Anda dapat adalah riset
dan rencana, tanpa ada yang berubah.

`bypassPermissions` untuk sandbox dan CI. Aturan deny tetap berlaku padanya; selain itu tidak ada.

## Aturan

Sebuah aturan berbentuk `Tool` atau `Tool(pola)`:

```jsonc
{
  "permissions": {
    "allow": [
      "Read",                      // semua pemanggilan Read
      "Bash(git status)",          // persis perintah ini
      "Bash(npm run:*)",           // awalan ini dan apa pun sesudahnya
      "Bash(dotnet *)",            // glob
      "Write(src/**)",             // glob jalur
      "mcp__github__*"             // semua tool dari satu server MCP
    ],
    "ask":  ["Bash(git push:*)"],  // paksa bertanya walau di acceptEdits
    "deny": ["Read(**/.env)", "Bash(rm -rf:*)", "Write(**/*.key)"]
  }
}
```

Sintaks pola:

| Bentuk | Arti |
| --- | --- |
| `Tool` | Semua pemanggilan tool tersebut |
| `Tool(persis)` | Cocok persis dengan subjeknya |
| `Tool(awalan:*)` | Subjek diawali `awalan` |
| `Tool(glob)` | Kecocokan glob, dengan `*`, `**`, `?`, `{a,b}` |

"Subjek" adalah perintah untuk tool shell, jalur untuk tool berkas, dan URL untuk tool jaringan.

## Urutan evaluasi

```
deny  →  mode  →  allow  →  ask
```

**Deny selalu menang.** Aturan deny tidak bisa dibatalkan oleh aturan allow yang lebih luas, dan juga
tidak oleh `bypassPermissions`. Inilah yang membuat daftar deny layak ditulis.

## Menjawab permintaan izin

```
╭─ Run command ─────────────────────────────────╮
│ Bash(dotnet test --filter OrderFlow)          │
╰───────────────────────────────────────────────╯
Allow this?
❯ Yes
  Yes, and don't ask again for Bash(dotnet test:*)
  No, tell Auto Code what to do differently
  No, and stop this turn
```

- **Yes** — hanya untuk pemanggilan ini.
- **Yes, and don't ask again** — menambahkan aturan allow untuk sisa sesi. Perintah digeneralisasi
  menjadi awalan; jalur tetap persis.
- **No, tell it what to do differently** — Anda mengetikkan alasan, agent menerimanya sebagai hasil
  tool, lalu menyesuaikan diri. Ini biasanya lebih berguna daripada penolakan kosong.
- **No, and stop** — mengakhiri giliran agar Anda bisa mengarahkan ulang.

Aturan sesi tidak ditulis ke disk. Pindahkan sendiri yang ingin Anda simpan ke `settings.json` —
agent yang diam-diam menumpuk izin lintas sesi bukan sifat yang Anda inginkan.

## Daftar deny untuk memulai

```jsonc
{
  "permissions": {
    "deny": [
      "Read(**/.env)", "Read(**/.env.*)", "Read(**/*.pem)", "Read(**/id_rsa*)",
      "Write(**/.env)", "Write(**/*.key)",
      "Bash(rm -rf:*)",
      "Bash(git push --force:*)",
      "Bash(curl:* | sh)",
      "Bash(sudo:*)"
    ]
  }
}
```

## Menjalankan tanpa interaksi

Pada mode `--print` tidak ada yang bisa bertanya, sehingga apa pun yang akan bertanya ditolak
disertai penjelasan yang bisa ditindaklanjuti model. Agar sesi headless boleh mengubah berkas:

```bash
autocode -p "perbaiki test yang gagal" --permission-mode acceptEdits
```

Atau beri izin persis sebatas yang diperlukan:

```bash
autocode -p "jalankan test" --allowed-tools "Bash(dotnet test:*)"
```

## Hook sebagai kebijakan

Hook `PreToolUse` bisa membatalkan pemanggilan sebelum mesin izin melihatnya — berguna untuk aturan
yang bergantung pada keadaan, bukan sekadar pada argumen, misalnya menolak penulisan ketika sedang
berada di branch rilis. Lihat [hooks](hooks.md).

## Subagent

Subagent mengalirkan setiap pemanggilan tool melalui mesin yang sama dengan loop utama. Mengirim
subagent bukan cara menghindari permintaan izin — Anda tetap akan ditanya. Subagent juga memulai
dengan pelacak baca berkas yang kosong, sehingga ia harus membaca sendiri sebuah berkas sebelum boleh
menyuntingnya.
