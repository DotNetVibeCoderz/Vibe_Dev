# Mode interaktif

> 🇬🇧 [English](../en/interactive-mode.md)

`dotcode` membuka UI terminal yang dibuat semirip mungkin dengan Claude Code: output yang sudah selesai ditulis sekali ke *scrollback* terminal, hanya area "live" di bawah (teks streaming, tool yang berjalan, spinner, kotak input, dialog) yang digambar ulang di tempat — dengan *synchronized update* sehingga tidak berkedip.

![Sesi membangun aplikasi konsol .NET](../images/console-app-done.png)

## Anatomi layar

| Elemen | Arti |
|---|---|
| `> prompt` (berlatar) | Pesan Anda |
| `●` + teks | Jawaban asisten (markdown: judul, daftar, tabel, kode dengan syntax highlight) |
| `● Tool(argumen)` | Pemanggilan tool — abu-abu/berkedip saat berjalan, hijau jika sukses, merah jika error |
| `⎿  ringkasan` | Ringkasan hasil tool; edit menampilkan diff berwarna dengan nomor baris |
| `☐ / ◼ / ☒` | Daftar tugas (belum / sedang / selesai) |
| `✻ Kata… (12s · ↓ 1.2k tokens · esc to interrupt)` | Indikator kerja dengan animasi kilau |
| Footer | Mode (`⏵⏵ accept edits on`, `⏵⏵ auto mode on`, `⏸ plan mode on`, `⏵⏵ bypass permissions on`), model, peringatan konteks |

Indikator kerja memutar glyph (`·✢✳✶✻✽`), menampilkan kilau pada kata kerja yang berganti-ganti selama turn berjalan (Pondering → Mulling → Whirring…), serta lama waktu (`16m 50s`), token output secara langsung (`↓ 66.4k tokens`), dan status thinking:

![Indikator kerja](../images/spinner.png)

## Pintasan keyboard

| Tombol | Aksi |
|---|---|
| `Enter` | Kirim (saat sibuk: masuk antrean) |
| `Shift+Enter`, `Alt+Enter`, `Ctrl+J`, `\` + `Enter` | Baris baru |
| `Shift+Tab` | Ganti mode izin: default → accept edits → (auto) → plan (→ bypass bila diaktifkan) |
| `Esc` | Hentikan turn · tutup menu · dua kali: kosongkan input |
| `Esc Esc` (input kosong) | Rewind ke pesan sebelumnya (percakapan dan/atau kode) |
| `Ctrl+C` | Kosongkan input · hentikan · dua kali untuk keluar |
| `Ctrl+D` | Keluar (input kosong) |
| `↑ / ↓` | Pindah baris di input multi-baris, selain itu riwayat prompt · navigasi menu |
| `Tab` | Terima saran |
| `Ctrl+O` | Output verbose (hasil tool lengkap, thinking) |
| `Ctrl+T` | Tampilkan daftar tugas |
| `Ctrl+L` | Bersihkan layar |
| `Ctrl+A/E`, `Ctrl+B/F`, `Alt+B/F` | Awal/akhir baris, gerak per karakter/kata |
| `Ctrl+U/K`, `Ctrl+W`, `Alt+D`, `Ctrl+Y` | Hapus ke awal/akhir, hapus kata, tempel |
| `Ctrl+_`, `Ctrl+Z` | Undo |
| `?` (input kosong) | Tampilkan pintasan |

## Awalan input

| Awalan | Mode |
|---|---|
| `/` | Slash command (autocomplete untuk bawaan, command kustom, skill, command plugin, prompt MCP) |
| `!` | Mode bash — jalankan perintah shell sendiri; outputnya masuk ke percakapan |
| `#` | Simpan ke memori (`DOTCODE.md`) |
| `@path` | Lampirkan file atau isi direktori; melengkapi path otomatis |

Tempelan panjang diringkas menjadi `[Pasted text #1 +42 lines]`.

## Slash command

| Perintah | Deskripsi |
|---|---|
| `/help` | Bantuan dan semua perintah |
| `/clear` | Hapus percakapan |
| `/compact [instruksi]` | Ringkas percakapan untuk membebaskan konteks |
| `/context` | Grid berwarna pemakaian konteks per kategori |
| `/cost` | Biaya, durasi, perubahan kode, token per model |
| `/model [provider:model]` | Pilih/atur model (semua provider terkonfigurasi) |
| `/effort [level]` | Upaya reasoning: off, low, medium, high, xhigh |
| `/theme` | Pemilih tema dengan pratinjau langsung |
| `/config` | Pengaturan: tema, set glyph (font), spinner, gaya input, reduced motion, thinking, tips, auto-compact… |
| `/output-style [nama]` | default, explanatory, learning, atau kustom |
| `/permissions` | Lihat/atur aturan izin |
| `/add-dir <path>` | Tambah direktori kerja |
| `/init` | Buat `DOTCODE.md` untuk repo |
| `/memory` | File memori; `/memory add <teks>` |
| `/resume [id]` | Lanjutkan sesi sebelumnya |
| `/rewind` | Pulihkan kode dan/atau percakapan |
| `/export [file]` | Ekspor percakapan ke Markdown |
| `/mcp` | Status server MCP; `/mcp reconnect <nama>` |
| `/agents`, `/skills`, `/hooks` | Daftar subagent, skill, hook |
| `/plugin` | Daftar/pasang/hapus plugin dan marketplace |
| `/todos`, `/bashes` | Daftar tugas; shell latar belakang |
| `/review`, `/security-review` | Review perubahan kode |
| `/status`, `/doctor` | Status sesi; diagnosis instalasi |
| `/about` | Versi dan kredit |
| `/exit` | Keluar |

![Menu slash command](../images/slash-commands.png)

## Dialog izin

![Dialog izin perintah shell](../images/permission-bash.png)

Pilihan: **Yes** · **Yes, and don't ask again for `<prefix>` commands** (disimpan ke `.dotcode/settings.local.json`) atau untuk edit **Yes, allow all edits during this session** · **No, and tell DotCode what to do differently** (`Esc`). Tombol `1`–`3`, `y`/`n`, panah, dan Enter berfungsi.

## Plan mode

Tekan `Shift+Tab` dua kali: model hanya boleh meneliti lalu menyajikan rencana lewat `ExitPlanMode`:

![Persetujuan rencana](../images/plan-mode.png)

## Tema dan font

Pilih dengan `/theme` (pratinjau langsung) atau `--theme <nama>`; simpan permanen dengan `dotcode theme set <nama>`.

Tema bawaan: `dark` (default), `light`, `dark-daltonized`, `light-daltonized`, `dark-ansi`, `light-ansi` (palet Claude Code) serta tema khas DotCode `dotnet`, `dracula`, `nord`, `solarized-dark`, `monokai`, `gruvbox`, `catppuccin`, `matrix`, `ocean-light`.

| Dracula | Nord | Catppuccin |
|---|---|---|
| ![](../images/theme-dracula.png) | ![](../images/theme-nord.png) | ![](../images/theme-catppuccin.png) |

UI terminal tidak bisa mengganti font terminal Anda, jadi DotCode menyesuaikan diri dengan **set glyph**: `unicode` (default, tampilan Claude Code), `ascii` (untuk font tanpa simbol — border dan spinner ikut berubah) dan `nerd` (ikon Nerd Font). Opsi gaya lain: gaya input (`lines` atau kotak `rounded`/`single`/`double`/`heavy`), spinner (`claude`, `dots`, `line`, `star`, `bounce`, `arc`, `dotnet`), warna aksen, dan reduced motion — semuanya di `/config`.

Tema kustom — `~/.dotcode/themes/senja.json`:

```json
{ "base": "dark", "displayName": "Senja", "colors": { "brand": "#FF7A59", "brandShimmer": "#FFC2AE", "userMessageBackground": "#2B1F2A" }, "border": "rounded", "spinner": "dots" }
```

## Sesi

Setiap percakapan disimpan sebagai JSONL. `dotcode -c` melanjutkan yang terakhir, `dotcode -r` membuka pemilih, `dotcode -r <id>` melanjutkan sesi tertentu (`--fork-session` menyalinnya). Saat keluar DotCode menampilkan biaya, durasi, dan perintah untuk melanjutkan.
