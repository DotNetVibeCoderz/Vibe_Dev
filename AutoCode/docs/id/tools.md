# Tools

> Auto Code — Gravicode Studios, dipimpin oleh Kang Fadhil

Tool adalah yang memungkinkan agent melakukan sesuatu selain berbicara. Setiap tool mendeklarasikan
kemampuannya, dan itulah yang menjadi dasar penjagaan oleh mesin izin.

| Tool | Kemampuan | Kegunaan |
| --- | --- | --- |
| `Read` | membaca berkas | Membaca potongan berkas dengan nomor baris |
| `Write` | menulis berkas | Membuat atau mengganti berkas |
| `Edit` | menulis berkas | Penggantian teks persis |
| `MultiEdit` | menulis berkas | Beberapa penggantian dalam satu transaksi |
| `Glob` | membaca berkas | Mencari berkas berdasarkan pola, terbaru dulu |
| `Grep` | membaca berkas | Pencarian regex pada isi berkas |
| `List` | membaca berkas | Menjelajah pohon direktori |
| `Bash` | menjalankan perintah | Menjalankan perintah shell |
| `TodoWrite` | tidak ada | Mengelola daftar tugas yang tampak |
| `WebFetch` | jaringan | Mengambil URL sebagai teks |
| `Task` | baca + tulis | Mengirim subagent |
| `CodeSearch` | membaca berkas | Pencarian semantik (bila indeks aktif) |
| `Verify` | menjalankan perintah | Menjalankan perintah verifikasi proyek |
| `mcp__*` | semua | Tool dari server MCP |

## Keamanan berkas

Tiga aturan yang ada justru karena ketiadaannya adalah cara agent merusak kode:

1. **Baca sebelum menyunting.** `Edit` dan `MultiEdit` menolak menyentuh berkas yang belum dibaca
   pada sesi ini. Agent harus melihat sebelum menambal.
2. **Kecocokan harus unik.** `Edit` gagal bila `old_string` muncul lebih dari sekali, kecuali
   `replace_all` diaktifkan. Menebak kemunculan mana yang dimaksud bukan kesalahan yang bisa
   dipulihkan.
3. **Deteksi basi.** Bila berkas berubah di disk sejak dibaca, penyuntingan ditolak. Agent akan
   membaca ulang dan mencoba lagi dengan isi terkini.

`Write` juga menolak menimpa berkas yang sudah ada dan tidak kosong bila berkas itu belum dibaca.

`MultiEdit` bersifat transaksional: bila ada satu suntingan gagal, tidak ada yang ditulis.

## Pencarian

`Glob` dan `Grep` otomatis melewati direktori build dan VCS — `.git`, `node_modules`, `bin`, `obj`,
`dist`, `target`, `__pycache__`, `.venv`, dan sekitar dua puluh lainnya.

```jsonc
// Grep mendukung tiga mode keluaran
{ "pattern": "class \\w+Service", "output_mode": "content", "-n": true, "-C": 2 }
{ "pattern": "TODO", "output_mode": "files_with_matches" }
{ "pattern": "TODO", "output_mode": "count", "glob": "**/*.cs" }
```

## Bash

Direktori kerja bertahan antar pemanggilan dalam satu sesi; variabel shell tidak, karena setiap
pemanggilan adalah proses baru.

- Timeout bawaan `bashTimeoutMs` (120 detik), maksimum 600 detik per pemanggilan.
- Keluaran dipotong pada 30.000 karakter.
- stdin langsung ditutup, sehingga perintah yang menunggu masukan gagal cepat alih-alih menggantung.
- Shell-nya `pwsh`/`powershell` di Windows dan `$SHELL` di sistem lain. Ubah dengan `"shell"`.

## Verify

`Verify` hanya muncul bila `verifyCommands` dikonfigurasi:

```jsonc
{ "verifyCommands": ["dotnet build", "dotnet test", "dotnet format --verify-no-changes"] }
```

Prompt sistem kemudian menginstruksikan agent menjalankannya setelah melakukan perubahan. Inilah
pembeda antara agent yang mengira sudah selesai dan agent yang tahu ia sudah selesai.

## CodeSearch

Pencarian semantik menemukan kode berdasarkan apa yang dilakukannya, bukan namanya — "di mana kita
memvalidasi refresh token" tetap berhasil walau tak satu pun kata itu muncul di kodenya.

```jsonc
// Lokal dan gratis — butuh Ollama berjalan
{
  "enableSemanticIndex": true,
  "embeddings": { "kind": "Ollama", "model": "nomic-embed-text" }
}
```

```jsonc
// Sepenuhnya offline — tanpa server, tanpa jaringan, tanpa key
{
  "enableSemanticIndex": true,
  "embeddings": {
    "kind": "Onnx",
    "modelPath": "models/model.onnx",
    "vocabPath": "models/vocab.txt"
  }
}
```

Embedding dikonfigurasi terpisah dari provider chat, sehingga indeks bisa berjalan di model lokal
sementara Anda bernalar dengan model daring. Lihat [provider](providers.md#embedding) untuk semua
backend.

Bangun indeksnya dengan `/index`. Indeks hidup di memori selama sesi; membangun ulang itu murah dan
selalu mutakhir, yang untuk basis kode yang sedang aktif disunting adalah pilihan yang tepat.

Pakai `Grep` bila Anda tahu simbol persisnya. `CodeSearch` untuk saat Anda tidak tahu.

## Membatasi kumpulan tool

```jsonc
{ "disabledTools": ["WebFetch", "mcp__*"] }
```

```bash
autocode --disallowed-tools WebFetch,Bash
autocode --allowed-tools Read,Grep,Glob     # menambah aturan allow, bukan daftar putih
```

Subagent punya daftar izinnya sendiri di definisinya — lihat [subagent](subagents.md).

## Membuat tool sendiri

Auto Code sengaja tidak menyediakan API plugin untuk tool terkompilasi: server MCP adalah titik
perluasan yang didukung, bekerja di semua klien MCP, dan tidak harus ditulis dengan C#. Lihat
[MCP](mcp.md).

Bila Anda menanamkan `AutoCode.Core` di aplikasi Anda sendiri, implementasikan `IAgentTool` dan
daftarkan pada `ToolRegistry` — antarmuka yang sama dipakai semua tool bawaan.
