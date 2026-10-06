# Konsep inti

[English](../en/concepts.md) · [Bahasa Indonesia](../id/concepts.md)

## Bot

**Bot** adalah rekan kerja AI yang tetap (*durable*). Bot memiliki identitas (nama, warna kelereng, peran), persona
(instruksi), profil model, pengaturan memori, skill, server MCP, paket *kernel function*, dan profil izin. Identitas
bot terpisah dari setiap eksekusi: satu bot yang sama dapat mengerjakan banyak tugas, di banyak utas, sekaligus.

## Boss Man

**Boss Man** adalah bot manajer yang dilindungi. Ia selalu ada, tidak bisa dihapus, dan menjadi pintu masuk obrolan
bawaan. Untuk permintaan kecil, ia menjawab langsung. Untuk tujuan yang lebih besar, ia:

1. melihat tim dengan `list_bots`,
2. memecah pekerjaan menjadi sub-tugas yang berdiri sendiri,
3. membagikannya dengan `delegate_tasks` (tugas yang independen berjalan paralel; `depends_on` mengatur urutan),
4. meninjau hasilnya dan membalas dengan satu sintesis.

Boss Man juga dapat merekrut anggota tim baru (`create_bot`, opsional dari templat) dan menjadwalkan pekerjaan rutin
(`schedule_task`). Anda selalu bisa melewati Boss Man dan mengobrol langsung dengan bot mana pun.

## Utas, tugas, dan eksekusi

| Istilah | Arti |
|---|---|
| **Utas (thread)** | Percakapan yang terlihat oleh manusia dengan satu bot. Bisa disematkan, diarsipkan, dicabangkan, di-reset, atau diekspor. |
| **Tugas (task)** | Unit kerja yang tahan lama. Setiap pesan yang Anda kirim membuat tugas akar; delegasi membuat tugas anak. |
| **Transkrip** | Pesan dan pemanggilan tool dari sebuah tugas. Tugas akar menulis ke utas; tugas anak memiliki transkrip sendiri (lihat Tugas → detail). |
| **Workspace** | Folder proyek per utas (`data/workspaces/<utas>`). Semua bot yang bekerja di utas tersebut berbagi folder ini, sehingga berkas dari developer terlihat oleh QA. |

Status tugas: `Queued → Preparing → Running ⇄ WaitingForTool / WaitingForAgent / WaitingForHuman → Completed | Failed | Cancelled | TimedOut`.

## Siklus agen

```
bangun konteks → panggil model → pemanggilan tool yang diperiksa kebijakan → amati → ulangi → jawaban akhir
```

Setiap langkah memancarkan event (`AgentThinkingStarted`, `ToolCallStarted`, `ToolCallCompleted`, …). Bot berhenti
saat model menjawab tanpa memanggil tool, atau saat mencapai batas **langkah maksimum**. Dalam kasus kedua, bot diminta
merangkum apa yang sudah selesai dan apa yang tersisa.

## Memori

- **Memori jangka pendek**: riwayat utas yang dilihat model. Matikan untuk bot tanpa status (*stateless*).
- **Memori jangka panjang**: fakta, preferensi, dan prosedur yang disimpan dengan `remember` (atau oleh Auto-Learn)
  dan dipanggil kembali secara otomatis. Pencarian memakai SQLite FTS5 (BM25) yang diurutkan ulang berdasarkan tingkat
  keyakinan dan kebaruan, serta difilter per pemilik. Setiap memori menyimpan asal-usulnya (sumber, tanggal, keyakinan).
- Memori bersifat **privat per bot**. Ruang `shared` dapat dibaca semua bot.

## Pemadatan konteks

Saat utas melewati ambang batas bot (bawaan 24.000 token), giliran lama dirangkum menjadi ringkasan bergulir
sementara dua giliran pengguna terakhir tetap utuh. Pesan tidak pernah dihapus, sehingga transkrip lengkap tetap
tersedia untuk audit. Ketik `/compact` di obrolan untuk memadatkan kapan saja. **Reset konteks** memulai model dari awal
tanpa menghapus riwayat.

## Auto-Learn

Opsional per bot:

| Mode | Yang terjadi setelah tugas |
|---|---|
| `Off` | Tidak ada |
| `MemoryOnly` | Hingga tiga fakta penting diekstrak ke memori jangka panjang (secret disaring) |
| `SuggestSkills` | Juga membuat draf `SKILL.md` yang dapat dipakai ulang ke antrean tinjauan (halaman Skill). Draf tidak pernah diterbitkan otomatis dan tidak pernah menambah izin. |

## Kernel function

Tool bawaan yang dikelompokkan dalam paket dan diaktifkan per bot:

| Paket | Tool |
|---|---|
| `files` | `read_file`, `write_file`, `edit_file`, `list_files`, `delete_file` |
| `search` | `grep` |
| `shell` | `run_shell` (PowerShell di Windows, bash di sistem lain) |
| `web` | `web_search`, `web_fetch` |
| `memory` | `remember`, `recall` |
| `todo` | `todo_write` (tampil langsung di obrolan) |
| `agents` | `list_bots`, `delegate_tasks` |
| `management` | Khusus Boss Man: `create_bot`, `list_templates`, `schedule_task`, `get_task` |
| `skills` | Ditambahkan otomatis jika bot punya skill: `load_skill`, `read_skill_file` |

## Event

Semua hal yang dapat diamati adalah event, disimpan di SQLite dan disebarkan secara langsung: UI obrolan, tampilan
Kantor, `logs` di CLI, dan endpoint SSE membaca aliran yang sama. Lihat [API dan SDK](api-and-sdks.md).

---
*Marbots — Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
