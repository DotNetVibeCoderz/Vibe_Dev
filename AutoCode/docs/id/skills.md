# Skills

> Auto Code — Gravicode Studios, dipimpin oleh Kang Fadhil

Skill adalah alur kerja yang Anda tuliskan sekali agar berjalan sama setiap kali. Ia menjadi slash
command, dan agent juga bisa memilihnya sendiri ketika sebuah tugas cocok.

## Struktur

```
.autocode/skills/
└── release/
    ├── SKILL.md          # wajib
    └── checklist.md      # berkas lain yang dirujuk skill ini
```

`SKILL.md` berisi front matter ditambah instruksi markdown:

```markdown
---
name: release
description: Buat rilis — naikkan versi, changelog, tag, publikasikan
allowed-tools: [Read, Edit, Bash, Grep]
---

Ikuti langkah berikut berurutan. Berhenti dan laporkan bila ada langkah yang gagal.

1. Pastikan working tree bersih (`git status --porcelain`).
2. Jalankan seluruh test. Jangan lanjut bila ada yang gagal.
3. Naikkan `VersionPrefix` di `Directory.Build.props` — patch kecuali diminta lain.
4. Tambahkan entri `CHANGELOG.md` dari commit sejak tag terakhir.
5. Commit dengan pesan `release: v<versi>`, lalu tag `v<versi>`.
6. Laporkan versi dan entri changelog-nya. Jangan push.
```

## Front matter

| Kolom | Kegunaan |
| --- | --- |
| `name` | Nama pemanggilan. Bawaannya nama direktori. |
| `description` | **Kapan skill ini dipakai.** Inilah yang dibaca agent untuk memutuskan. |
| `allowed-tools` | Batasi skill hanya pada tool ini. Opsional. |
| `model` | Jalankan alur kerja ini pada model lain. Opsional. |
| `templated` | Aktifkan prompt templating Semantic Kernel. Opsional. |

`description` yang menentukan segalanya. Tulislah sebagai pemicu, bukan judul:

- Lemah: *"Skill rilis"*
- Kuat: *"Buat rilis — dipakai saat diminta merilis, memberi tag, atau memublikasikan versi baru"*

## Di mana skill dicari

Lokasi berikutnya menimpa yang sebelumnya berdasarkan nama:

1. `~/.autocode/skills/` — milik Anda, semua proyek
2. `<workspace>/.autocode/skills/` — milik proyek
3. `<workspace>/.claude/skills/` — tata letak Claude Code, dibaca apa adanya
4. Apa pun yang ada di `skillDirectories`
5. Skill dari plugin

## Memakainya

```
› /release
› /release hanya naikkan versi patch
```

Apa pun setelah nama perintah dikirim sebagai argumen dan dilampirkan di bawah judul `## Arguments`.

Skill juga tercantum di prompt sistem beserta deskripsinya, sehingga agent bisa memanggilnya sendiri
saat tugasnya cocok — dan itulah gunanya menulis deskripsi yang baik.

Lihat yang terpasang:

```
› /skills
```

## Templating

Set `templated: true` untuk memakai sintaks prompt template Semantic Kernel:

```markdown
---
name: review-pr
description: Tinjau pull request berdasarkan nomornya
templated: true
---

Tinjau pull request #{{$arguments}}.

Ambil dengan `gh pr view {{$arguments}} --json title,body,files`, baca setiap berkas yang berubah,
dan laporkan cacat beserta skenario kegagalan konkretnya masing-masing.
```

Baik `{{$arguments}}` maupun `{{$input}}` terisi oleh apa pun yang mengikuti perintah.

Tanpa `templated: true`, isi berkas diteruskan apa adanya — dan itu yang Anda inginkan pada
kebanyakan kasus, karena kurung kurawal di contoh kode tetap menjadi kurung kurawal.

## Menulis skill yang benar-benar berguna

**Tegas soal urutan dan kondisi berhenti.** "Jalankan test" lebih lemah daripada "Jalankan test;
jangan lanjut bila ada yang gagal."

**Kodekan hal yang sering terlupa.** Nilai sebuah skill bukan pada jalur mulusnya — itu bisa
disimpulkan sendiri oleh agent. Nilainya ada pada langkah yang terus terlewat oleh tim Anda.

**Sebutkan yang tidak boleh dilakukan.** "Jangan push" dan "jangan amend commit yang sudah ada"
mencegah lebih banyak kerusakan daripada instruksi positif mana pun.

**Cukup satu alur kerja.** Skill yang mengerjakan tiga hal tak berhubungan akan terpilih untuk hal
yang salah.

## Contoh: alur kerja debugging

```markdown
---
name: debug
description: Selidiki test yang gagal atau laporan bug secara metodis
allowed-tools: [Read, Grep, Glob, Bash, Edit]
---

Kerjakan berurutan. Jangan melompat langsung ke perbaikan.

1. Reproduksi. Jalankan test yang gagal atau skenario yang dilaporkan, dan tempelkan keluaran aslinya.
2. Baca kode yang muncul di stack trace — kode yang sebenarnya, bukan yang Anda kira tertulis di sana.
3. Susun satu hipotesis dan nyatakan secara eksplisit.
4. Uji hipotesis itu dengan perubahan sekecil mungkin atau print yang terarah.
5. Baru setelah terbukti, tulis perbaikannya.
6. Jalankan ulang kasus yang gagal, lalu seluruh test.

Laporkan: akar masalah, perbaikannya, dan bukti bahwa perbaikan itu bekerja.
Bila langkah 3 dan 4 membantah hipotesis Anda, katakan demikian dan mulai lagi dari langkah 2,
bukan menebak-nebak.
```
