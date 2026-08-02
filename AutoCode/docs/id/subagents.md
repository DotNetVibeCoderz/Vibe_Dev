# Subagent dan agent team

> Auto Code — Gravicode Studios, dipimpin oleh Kang Fadhil

## Mengapa subagent ada

Sebagian langkah menghasilkan jauh lebih banyak keluaran daripada kesimpulan. Menyisir empat puluh
berkas untuk menemukan tiga tempat pemanggilan akan memenuhi konteks utama Anda dengan tiga puluh
tujuh berkas yang tidak Anda perlukan.

Subagent menjalankan pekerjaan itu di konteksnya sendiri dan hanya mengembalikan laporannya.
Percakapan utama menerima jawabannya, bukan proses pencariannya.

Gunakan subagent ketika:

- Menjawab berarti membaca banyak berkas dan Anda hanya ingin kesimpulannya.
- Anda ingin pendapat independen — peninjau yang tidak menyaksikan Anda menulis kodenya.
- Pekerjaannya berdiri sendiri dan langkah antaranya tidak penting.

Jangan gunakan subagent bila tugasnya kecil, atau bila tugas itu memerlukan konteks percakapan untuk
bisa dipahami — subagent tidak bisa melihatnya.

## Agent bawaan

Tersedia ketika sebuah workspace belum mendefinisikan agent-nya sendiri:

| Agent | Tool | Kegunaan |
| --- | --- | --- |
| `explorer` | hanya baca | Mencari dan membaca; melaporkan temuan dengan jalur berkas dan nomor baris |
| `reviewer` | hanya baca + Bash | Meninjau perubahan untuk mencari cacat dengan skenario kegagalan konkret |
| `tester` | baca + tulis + Bash | Menulis dan menjalankan test, mengikuti konvensi suite yang ada |

## Membuat sendiri

```
.autocode/agents/migration-auditor.md
```

```markdown
---
name: migration-auditor
description: Mengaudit migrasi EF Core untuk perubahan destruktif atau yang belum ditinjau
tools: [Read, Grep, Glob, Bash]
max-iterations: 30
model: gpt-4.1-mini
---

Anda mengaudit migrasi Entity Framework sebelum sampai ke produksi.

Baca setiap migrasi di bawah `Migrations/` yang lebih baru dari tag rilis terakhir. Untuk masing-
masing, tentukan apakah ia menghapus kolom, menghapus tabel, mengubah tipe kolom menjadi lebih
sempit, atau menambah kolom non-nullable tanpa nilai bawaan — itulah perubahan yang menghilangkan
data.

Laporkan setiap temuan sebagai: berkas migrasinya, operasi spesifiknya, dan data apa yang akan
hilang. Bila sebuah migrasi aman, jangan disebutkan. Bila tidak ada temuan, katakan dalam satu baris.
```

| Kolom | Kegunaan |
| --- | --- |
| `name` | Nama pengiriman |
| `description` | **Kapan agent ini dikirim.** Agent utama membaca ini untuk memilih. |
| `tools` | Daftar izin. Kosong berarti semuanya kecuali `Task`. |
| `model` / `provider` | Jalankan pada model lain, sering kali yang lebih murah |
| `max-iterations` | Batas putaran pemanggilan tool milik subagent |

Dimuat dari `~/.autocode/agents/`, `<workspace>/.autocode/agents/`, dan
`<workspace>/.claude/agents/`, dengan prioritas menaik, ditambah apa pun yang disumbang plugin.

## Mengirim subagent

Agent melakukannya sendiri lewat tool `Task` ketika ada tugas yang cocok dengan sebuah deskripsi.
Anda juga bisa memintanya langsung:

```
› pakai agent explorer untuk menemukan semua tempat kita membuat HttpClient
```

Lihat yang tersedia:

```
› /agents
```

## Isolasi, secara persis

Sebuah subagent mendapatkan:

- Prompt sistemnya sendiri, dari definisinya.
- Percakapannya sendiri. Ia tidak bisa melihat percakapan induk, sehingga prompt-nya harus berdiri
  sendiri.
- Pelacak baca berkasnya sendiri — ia harus membaca sendiri sebuah berkas sebelum boleh
  menyuntingnya, terlepas dari apa yang sedang terbuka di induknya.
- Kumpulan tool tersaring, tanpa `Task`. **Penyarangan dimatikan**: subagent yang bisa memunculkan
  subagent mengubah proses yang terbatas menjadi tak terbatas, pada kedalaman yang tidak disetujui
  siapa pun.

Yang dibagi bersama: workspace, terminal, dan — yang penting — **mesin izin**. Mengirim subagent
bukan cara menghindari permintaan persetujuan.

Subagent berjalan di atas Microsoft Agent Framework (`ChatClientAgent`), dengan pemanggilan tool
dialirkan melalui pipeline penjagaan yang sama dengan loop utama.

## Agent team

Tim menugaskan beberapa subagent pada satu brief.

```jsonc
{
  "teams": {
    "review": {
      "description": "Peninjauan menyeluruh atas perubahan saat ini",
      "mode": "Parallel",
      "members": ["reviewer", "tester", "migration-auditor"],
      "synthesizer": "reviewer"
    },
    "feature": {
      "description": "Riset, lalu implementasi, lalu pengujian",
      "mode": "Sequential",
      "members": ["explorer", "implementer", "tester"]
    }
  }
}
```

**Parallel** mengirim brief yang sama ke semua anggota sekaligus lalu menggabungkan laporannya. Isi
`synthesizer` dan ia akan merekonsiliasi hasilnya — mempertahankan yang disepakati, memunculkan yang
bertentangan, membuang klaim tanpa dasar. Pakai ini ketika sudut pandang independen adalah intinya.

**Sequential** mengalirkan keluaran tiap anggota ke anggota berikutnya, bersama brief aslinya. Pakai
ini ketika pekerjaannya bertahap.

```
› /teams review perubahan di src/Api/Orders sejak commit terakhir
```

Pada mode paralel para anggota berjalan bersamaan, sehingga tim beranggota tiga memakan waktu kurang
lebih setara satu anggota — dan token setara tiga anggota.

## Biaya

Setiap subagent adalah percakapan terpisah dengan prompt sistemnya sendiri. Tim paralel beranggota
tiga melipattigakan biaya token untuk langkah itu. Itu biasanya sepadan untuk peninjauan, di mana
independensi adalah nilainya; jarang sepadan untuk pencarian yang bisa dijawab satu `Grep`.

Isi `model` pada definisi yang tidak memerlukan model terbaik Anda — explorer yang berjalan di model
kecil mengembalikan jalur berkas yang sama dengan biaya jauh lebih murah.
