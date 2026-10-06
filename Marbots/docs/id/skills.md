# Skill

[English](../en/skills.md) · [Bahasa Indonesia](../id/skills.md)

**Skill** adalah folder berisi berkas `SKILL.md` (ditambah `scripts/`, `references/`, `templates/`, `assets/` jika
perlu). Formatnya kompatibel dengan Claude/Agent Skills dan skill gaya OpenClaw.

```markdown
---
name: weekly-report
description: Membuat laporan KPI mingguan sesuai gaya perusahaan
version: 1.0.0
requires:
  tools: [read_file, write_file]
permissions:
  network: false
  shell: false
---

# Laporan mingguan
1. Baca data/kpi.csv ...
```

## Pengungkapan bertahap (progressive disclosure)

Hanya **nama dan deskripsi** setiap skill yang masuk ke system prompt bot. Saat bot memutuskan memakai sebuah skill,
ia memanggil `load_skill` untuk membaca instruksi lengkap, dan `read_skill_file` untuk templat atau referensi yang
dibundel. Dengan cara ini bot bisa mengenal banyak skill tanpa memenuhi jendela konteksnya.

## Skill bawaan

`api-design`, `bilingual-docs`, `code-review`, `data-analysis`, `docker-devops`, `dotnet-engineering`,
`financial-analysis`, `legal-review`, `market-research`, `marketing-copy`, `meeting-notes`, `product-requirements`,
`python-scripting`, `recruiting`, `report-writing` (dengan templat laporan HTML), `security-review`,
`spreadsheet-builder`, `test-automation`, `ux-design-brief`, `web-app-builder`.

## Memasang dan membuat skill

![Galeri skill](../images/skills.png)

- **Dari git atau folder**: Skill → *Install*, atau `marbots skills install https://github.com/org/skills.git`. Setiap
  `SKILL.md` yang ditemukan disalin ke `data/skills/`.
- **Buat di UI**: Skill → *New skill*.
- **Aktifkan per bot**: Tim → Ubah → Skills. Boss Man memakai `*` (semua skill).

Label kepercayaan: `Marbots Verified` (bawaan), `Local` (dipasang), `Unverified` (draf auto-learn). Tabel juga
menunjukkan apakah skill butuh shell atau jaringan dan apakah membundel skrip. Tinjau skrip sebelum memberikan skill
kepada bot yang punya akses shell.

## Skill hasil auto-learn

Bot dengan mode `SuggestSkills` dapat membuat draf skill baru dari tugas multi-langkah yang berhasil. Draf masuk ke
**Waiting for review** di halaman Skill. **Publish skill** memindahkan draf ke `data/skills`; **Discard** menghapusnya.
Skill hasil belajar tidak pernah memberi izin baru: ia berjalan di dalam profil izin bot yang sudah ada.

---
*Marbots — Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
