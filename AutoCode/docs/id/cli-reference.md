# Rujukan CLI

> Auto Code — Gravicode Studios, dipimpin oleh Kang Fadhil

```
autocode [opsi] [prompt]
autocode <perintah> [argumen]
```

## Perintah

| Perintah | Kegunaan |
| --- | --- |
| `config show` | Konfigurasi hasil resolusi dan asalnya |
| `config init` | Buat kerangka `.autocode/settings.json` |
| `config path` | Berkas settings yang sedang dibaca |
| `mcp` | Server MCP yang dikonfigurasi |
| `sessions` | Sesi tersimpan untuk workspace ini |
| `sessions rm <id>` | Hapus salah satu |
| `doctor` | Verifikasi instalasi, konfigurasi, dan permintaan sungguhan ke model |

## Opsi

| Opsi | Arti |
| --- | --- |
| `-p`, `--print` | Jalankan sekali, cetak hasilnya, keluar |
| `--output-format <fmt>` | `text` (bawaan), `json`, `stream-json` |
| `-c`, `--continue` | Lanjutkan sesi terakhir di workspace ini |
| `-r`, `--resume <id>` | Lanjutkan sesi tertentu |
| `--model <id>` | Ganti model untuk sekali jalan ini |
| `--provider <nama>` | Ganti profil provider |
| `--permission-mode <m>` | `ask`, `acceptEdits`, `plan`, `bypassPermissions` |
| `--allowed-tools <daftar>` | Aturan allow dipisah koma |
| `--disallowed-tools <daftar>` | Nama tool yang dimatikan, dipisah koma |
| `--dangerously-skip-permissions` | Sama dengan `--permission-mode bypassPermissions` |
| `--cwd <jalur>` | Akar workspace yang dipakai |
| `--lang <en\|id>` | Bahasa antarmuka dan jawaban |
| `--no-context` | Lewati pencarian `AUTOCODE.md` / `CLAUDE.md` |
| `-h`, `--help` | Tampilkan cara pakai |
| `-v`, `--version` | Tampilkan versi |

## Slash command

Tersedia di dalam sesi interaktif.

Ketik `/` dan daftarnya muncul, lengkap dengan keterangan singkat tiap perintah. Tab mengisi sejauh
semua kandidat sepakat; tombol panah memilih salah satu.

![Pelengkapan slash command](../assets/autocode-completion.png)

**Konteks**

| Perintah | Kegunaan |
| --- | --- |
| `/context` | Pemakaian token dan berkas instruksi yang berlaku |
| `/compact [instruksi]` | Ringkas percakapan; instruksinya mengarahkan apa yang dipertahankan |
| `/clear` | Mulai percakapan baru |
| `/memory [catatan]` | Tinjau atau tambahkan ke `AUTOCODE.md`, instruksi tetap proyek ini |
| `/btw [pertanyaan]` | Bertanya di thread samping — dijawab tanpa menyentuh percakapan utama |

**Sesi**

| Perintah | Kegunaan |
| --- | --- |
| `/rename <judul>` | Beri nama sesi ini |
| `/resume`, `/sessions` | Daftar sesi tersimpan untuk workspace ini |
| `/branch` | Cabangkan percakapan, untuk menjajaki alternatif |
| `/undo [n]`, `/rewind [n]` | Buang n pertukaran terakhir lalu lanjut dari sana |
| `/export [jalur]` | Tulis transkrip ke berkas markdown |
| `/recap` | Ringkasan satu paragraf dari sesi ini sejauh ini |

**Konfigurasi**

| Perintah | Kegunaan |
| --- | --- |
| `/model [id]` | Tampilkan atau ganti model |
| `/provider [nama]` | Tampilkan atau ganti profil provider |
| `/effort [level]` | Kedalaman penalaran: off, low, medium, high, max |
| `/permissions [mode]` | Tampilkan atau atur ask, acceptEdits, plan, bypassPermissions |
| `/config` | Dari mana setelan dibaca, dan cara menyuntingnya |
| `/theme [nama]` | Ganti tema warna: auto, plain |
| `/language <en\|id>` | Bahasa antarmuka dan jawaban |

**Alur kerja**

| Perintah | Kegunaan |
| --- | --- |
| `/plan` | Masuk mode rencana: hanya riset, tanpa perubahan |
| `/diff [jalur]` | Tampilkan perubahan yang belum di-commit |
| `/code-review` | Tinjau diff kerja untuk mencari cacat |
| `/security-review` | Tinjau diff kerja untuk mencari kerentanan |
| `/init` | Buatkan `AUTOCODE.md` untuk proyek ini |
| `/index` | Bangun indeks kode semantik |

**Periksa**

| Perintah | Kegunaan |
| --- | --- |
| `/status` | Provider, model, workspace, izin, sesi |
| `/cost` | Perhitungan token dan biaya |
| `/tools` | Tool yang tersedia bagi model |
| `/agents` | Subagent yang bisa dikirim |
| `/teams <nama> <brief>` | Jalankan sebuah agent team |
| `/fork <agent> <brief>` | Serahkan tugas sampingan ke subagent, di luar percakapan utama |
| `/skills` | Skill yang terpasang |
| `/mcp` | Status koneksi MCP |
| `/tasks` | Subagent yang sedang berjalan |
| `/about` | Tentang Auto Code |
| `/help` | Daftar perintah ini |
| `/exit` | Keluar |

Setiap skill yang terpasang juga menjadi slash command: `/deploy`, `/release`, dan seterusnya.

## Masukan

- Enter mengirim.
- Akhiri baris dengan `\` untuk melanjutkan ke baris berikutnya, untuk prompt banyak baris.
- **Ctrl+C** membatalkan giliran yang sedang berjalan; sesi tetap hidup. Tekan lagi saat prompt
  menganggur untuk keluar.

## Format keluaran

**`text`** *(bawaan)* — teks asisten, dialirkan.

**`json`** — satu objek, setelah proses selesai:

```json
{
  "session_id": "a1b2c3d4e5f6",
  "model": "gpt-4.1",
  "provider": "openai",
  "result": "…",
  "denials": [],
  "usage": { "input_tokens": 1420, "output_tokens": 310, "requests": 3, "cost_usd": 0.0053 }
}
```

**`stream-json`** — JSON dipisah baris baru, satu objek per peristiwa, saat terjadi:

```json
{"type":"turn_started","session_id":"a1b2c3d4","iteration":1,"timestamp":"…"}
{"type":"tool_call_started","call_id":"c1","tool_name":"Read","summary":"Read(src/a.cs)","timestamp":"…"}
{"type":"tool_call_completed","call_id":"c1","tool_name":"Read","success":true,"display":"Read src/a.cs (120 lines)","elapsed_ms":8,"timestamp":"…"}
{"type":"assistant_text","text":"Berkas ini mendefinisikan…","is_final":false,"timestamp":"…"}
{"type":"turn_completed","iterations":2,"input_tokens":1420,"output_tokens":310,"cost_usd":0.0053,"elapsed_ms":4210,"timestamp":"…"}
```

Jenis peristiwa: `turn_started`, `assistant_text`, `assistant_thinking`, `tool_call_started`,
`tool_call_completed`, `tool_call_denied`, `todo_updated`, `subagent_started`, `subagent_completed`,
`compaction`, `notice`, `error`, `turn_completed`.

## Exit code

| Kode | Arti |
| --- | --- |
| `0` | Berhasil |
| `1` | Galat, atau giliran dibatalkan |
| `2` | Salah pemakaian — opsi tidak dikenal, atau `--print` tanpa prompt |
| `130` | Dibatalkan |

## Environment variable

| Variabel | Kegunaan |
| --- | --- |
| `AUTOCODE_PROVIDER` | Profil provider, sama dengan `--provider` |
| `AUTOCODE_MODEL` | Id model, sama dengan `--model` |
| `AUTOCODE_<JALUR>` | Pengaturan apa pun, `__` sebagai pemisah bagian |
| `AUTOCODE_DEBUG=1` | Cetak stack trace lengkap saat gagal |
| `NO_COLOR` | Matikan keluaran berwarna |
| `OPENAI_API_KEY` dll. | Key vendor yang dikenali — lihat [provider](providers.md) |

## Contoh

```bash
autocode
autocode "kenapa build gagal di CI?"
autocode -p "daftar semua endpoint publik" --output-format json | jq -r .result
autocode --continue
autocode --permission-mode plan "bagaimana cara menambahkan multi-tenancy?"
autocode --provider ollama --model qwen2.5-coder:14b
git diff --staged | autocode -p "tuliskan pesan commit untuk perubahan ini"
autocode -p "perbaiki test yang gagal" --permission-mode acceptEdits
autocode doctor
```

## Perintah yang sengaja tidak ada

Auto Code berjalan sepenuhnya di terminal Anda, pada sesi yang Anda mulai sendiri. Empat perintah
yang sering diminta mengandaikan infrastruktur yang tidak dimilikinya, dan stub bertuliskan "segera
hadir" lebih buruk daripada tidak ada sama sekali:

| Perintah | Alasannya |
| --- | --- |
| `/background` | Tidak ada daemon sesi. Satu giliran hidup dan mati bersama prosesnya. |
| `/teleport` | Tidak ada sesi web untuk ditarik. |
| `/remote-control` | Tidak ada server yang menyimpan sesi untuk diakses perangkat lain. |
| `/batch` | Memecah perubahan besar menjadi unit independen adalah pertimbangan perencanaan, bukan mekanisme. Gunakan `/plan`, lalu kerjakan hasilnya. |

`/focus` juga tidak ada: antarmukanya memang tidak punya elemen untuk disembunyikan.

`/tasks` ada, tetapi akan selalu melaporkan tidak ada yang berjalan — subagent dieksekusi inline di
dalam giliran yang mengirimnya, sehingga tidak pernah ada yang terpisah.
