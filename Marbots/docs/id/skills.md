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


## Minta skill ke Boss Man

Misalnya: "Kirana ingin membuat poster seni generatif; carikan skill yang cocok untuknya." Boss Man bekerja seperti
ini:

1. `list_skill_catalog` menampilkan skill yang sudah tersedia (bawaan atau terpasang) dan katalog terkurasi: skill
   resmi Anthropic, seperti pptx, docx, xlsx, pdf, frontend-design, webapp-testing, canvas-design, dan algorithmic-art.
2. `install_skill` langsung memberikan skill yang sudah tersedia ke bot yang disebut.
3. Skill dari katalog diunduh dulu, dan hanya setelah Anda menyetujui. Kartunya menampilkan sumber dan peringatan bahwa
   skill bisa berisi skrip. Hanya skill tersebut yang disalin dari repositori.

Skill di luar katalog dipasang oleh manusia di halaman Skills. Skill tidak pernah memperluas profil izin bot.

## Evaluasi pembelajaran

Setiap tugas yang memuat skill dihitung untuk **versi** skill tersebut: tugas selesai adalah keberhasilan, tugas gagal
adalah kegagalan, dan tugas yang dibatalkan tidak dihitung. Halaman Skills menampilkan putusan untuk setiap skill,
begitu pula `marbots skills` dan `GET /api/v1/skills/evaluations`:

| Putusan | Kapan |
|---|---|
| Collecting evidence | kurang dari 3 kali dijalankan |
| Healthy | keberhasilan minimal 80% |
| Underperforming | di bawah 80% dan tidak ada versi sebelumnya yang lebih baik |
| Rollback recommended | di bawah 50%, dan versi sebelumnya lebih baik (atau belum punya data) |
| Ready to publish | draf auto-learn yang diuji coba dengan keberhasilan 80% atau lebih |
| Discard recommended | draf yang di bawah 50% saat diuji coba |

- **Uji coba.** Bot yang menyusun sebuah skill (auto-learn) boleh memuatnya sebelum diterbitkan. Draf mengumpulkan
  bukti, tetapi penerbitan tetap keputusan manusia (`marbots skills promote <nama>` atau **Publish skill**).
- **Versi dan rollback.** Menerbitkan versi baru dari skill yang sudah terpasang menaikkan nomor versinya dan menyimpan
  versi lama di `data/skills-history`. **Roll back** (atau `marbots skills rollback <nama>`, atau
  `POST /api/v1/skills/{name}/rollback`) memulihkan versi sebelumnya dan menyimpan versi yang diganti, sehingga
  rollback pun bisa dibatalkan.
- **Rollback otomatis.** Opsional: **Roll back automatically** di halaman Skills, atau
  `marbots skills auto-rollback on`. Bila sebuah versi terus gagal dan evaluasi merekomendasikan rollback, rollback
  terjadi dengan sendirinya, disertai event `SkillRolledBack` di log.
- **Berkas skill.** Memuat skill menyalin berkasnya (skrip, templat) ke workspace di `.skills/<nama>/`, juga di
  komputer jarak jauh, sehingga bot bisa menjalankan skrip skill.

---
*Marbots — Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
