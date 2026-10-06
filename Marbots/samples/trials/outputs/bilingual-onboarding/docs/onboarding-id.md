# Nusantara Kopi Tech — Panduan Onboarding Karyawan Baru

Tanggal: 2026-10-06 · Penulis: Wren (panduan onboarding)

## Introduction

Selamat datang di Nusantara Kopi Tech. Misi kami adalah membangun perangkat lunak yang menyenangkan dan dapat diandalkan untuk menghubungkan petani kopi Indonesia, pemanggang lokal, dan konsumen menggunakan teknologi yang cerdas dan manusiawi. Kami adalah tim kecil lintas-fungsi berjumlah 20 orang: engineer, produk, desain, operasi, dan customer success. Kami bergerak cepat, sering merilis, dan menghargai rasa hormat, pembelajaran, serta solusi yang praktis.

Budaya kami ramah, pragmatis, dan inklusif. Kami berharap semua orang saling menghormati, bertanya lebih awal ketika ragu, dan mendokumentasikan keputusan agar pengetahuan tetap bersama tim. Kami menghargai kejelasan di atas kecanggihan dan kode yang bekerja daripada kesempurnaan.

---

## 1) First week checklist

Tujuan: menjadi produktif dan nyaman. Target penyelesaian: akhir Minggu 1. Jika ada yang terblokir, beri tahu HR dan manajer Anda segera.

Hari 0 / Pra-tiba
- Konfirmasi tanggal mulai dengan HR (kontak: HR: Sari — slack: @sari · email: sari@nusantarakopi.tech).
- Isi dokumen pra-onboarding: https://confluence.nusantarakopi.internal/onboarding-forms (placeholder).

Hari 1 — Akun & perangkat
- Terima perangkat (laptop + adaptor). Jika remote, konfirmasi pengiriman (IT: Dedi — @dedi · it@nusantarakopi.tech).
- Masuk ke Google Workspace dan atur email perusahaan.
- Bergabung ke Slack (undangan dikirim oleh HR). Konfirmasi nama tampilan Slack dan zona waktu Anda.

Hari 2 — Akses & dokumen
- Minta akses GitHub ke repositori inti (lihat Tools & accounts). Izin standar: read + triage; minta write dari Team Lead Anda.
- Minta akses Jira dan tambahkan ke papan proyek Anda.
- Verifikasi akses ke dokumen internal: Confluence / Notion link placeholder: https://confluence.nusantarakopi.internal/handbook
- Baca: Company handbook, Engineering README, Deployment guide, On-call/Incident runbook.

Hari 3 — Kenalan dengan tim
- Pertemuan pengantar dengan Team Lead Anda (30–60 menit) — agenda: ekspektasi peran, tugas awal.
- Pengantar singkat dengan CTO/Engineering Manager (20 menit).
- 1:1 dengan HR untuk konfirmasi benefit dan kebijakan.
- Perkenalan tim: hadiri daily standup dan sinkron tim.

Hari 4 — Tugas pertama
- Pilih tugas onboarding kecil (perbaikan bug, pembersihan dokumen, atau fitur kecil) dari label onboarding di GitHub.
- Buka PR/MR (lihat praktik tinjauan kode) dan minta reviewer.
- Pair dengan rekan untuk walkthrough kode.

Hari 5 — Tinjau & rencanakan
- Rayakan PR pertama yang ter-merge dan terima masukan.
- Rencanakan sprint dua minggu berikutnya dengan Team Lead.
- Konfirmasi rapat berulang dan undangan kalender.

Checklist prioritas:
- [ ] Email perusahaan aktif
- [ ] Bergabung Slack
- [ ] Akses GitHub
- [ ] Akses Jira
- [ ] Laptop terkonfigurasi
- [ ] PR pertama dibuka
- [ ] Membaca handbook + dokumen deployment

---

## 2) Tools & accounts

Kami menggunakan tumpukan kecil dan andal yang umum di startup berukuran 20 orang. Jika Anda memerlukan alat lain, diskusikan dengan manajer Anda.

- Slack — Tujuan: komunikasi sehari-hari, channel untuk tim dan proyek. Izin default: member workspace. Cara mendapatkan akses: HR akan mengundang; hubungi IT atau HR untuk undangan ulang. Kontak: IT: @dedi · it@nusantarakopi.tech.

- Google Workspace (Gmail, Drive, Calendar) — Tujuan: email, dokumen, kalender. Izin default: pengguna standar. Cara mendapatkan akses: disediakan oleh IT/HR pada hari pertama.

- GitHub (repositori + GitHub Actions untuk CI/CD) — Tujuan: kode sumber, PR, CI. Izin default untuk karyawan baru: read + triage. Untuk mendapatkan write: minta dari Team Lead. Cara meminta: buka tiket ke IT atau minta Team Lead menambahkan Anda. Kontak: Eng Manager: @angga · angga@nusantarakopi.tech.

- Jira — Tujuan: pelacakan tugas, sprint, backlog. Izin default: browse + comment. Cara meminta: HR akan mengirim undangan; jika tidak, minta Team Lead Anda.

- CI/CD (GitHub Actions) — Tujuan: pipeline build & deploy otomatis. Izin tipikal: pipeline berjalan otomatis; hanya engineer senior yang memiliki izin deploy secara default. Untuk meminta izin deploy, bicarakan dengan Engineering Manager dan baca checklist deployment.

- Akses lingkungan Dev / Staging — Tujuan: menguji perubahan sebelum produksi. Cara meminta: buat tiket akses ke IT dengan GitHub handle Anda dan alasan akses.

- VPN / Akses remote — Tujuan: akses aman ke layanan internal. Cara meminta: IT menyediakan kredensial (dianjurkan 2FA). Jika butuh VPN, hubungi IT.

- Manajer kata sandi (1Password atau LastPass) — Tujuan: penyimpanan kredensial bersama. Izin default: undangan ke vault perusahaan. Cara meminta: IT akan mengundang; untuk rahasia tambahan, ikuti proses permintaan kredensial.

- Dokumen internal (Confluence / Notion) — Tujuan: handbook perusahaan, runbook, tugas onboarding. Link: https://confluence.nusantarakopi.internal/handbook (placeholder). Minta Team Lead atau HR untuk halaman yang hilang.

Untuk semua permintaan akun: sertakan nama lengkap, GitHub handle, Slack handle, dan tanggal mulai. Perkirakan 1–3 hari kerja untuk sebagian besar permintaan.

---

## 3) Ways of working

Ritme rapat
- Daily standup — 15 menit, channel tim, setiap hari kerja. Tujuan: hilangkan hambatan.
- Weekly planning — 60–90 menit, tim + produk, tentukan tujuan sprint.
- Weekly demo / show-and-tell — 30 menit, opsional namun dianjurkan.
- Retrospective — setiap dua minggu di akhir sprint.

Komunikasi
- Slack untuk chat cepat; gunakan thread agar konteks terjaga. Perkiraan waktu tanggapan: dalam hari yang sama untuk pesan penting, 24 jam untuk non-urgent. Gunakan email untuk topik formal atau panjang.
- Gunakan indikator status (away / focus) dan perbarui status Slack saat rapat atau deep work.

Praktik tinjauan kode
- Buka PR lewat GitHub. Gunakan PR kecil dan terfokus (ideal < 400 baris). Tambahkan deskripsi jelas, langkah testing, dan link ke tiket Jira.
- SLA review: minta setidaknya satu reviewer; target tanggapan review dalam 24 jam kerja. Merge hanya setelah persetujuan dan CI hijau.
- Strategi merge: gunakan feature branches dan gunakan Squash-and-merge untuk riwayat yang rapi. Untuk hotfix gunakan pola branch hotfix.

Branching & proses rilis
- Penamaan branch: feature/<deskripsi-singkat>, fix/<nomor-tiket>, chore/<deskripsi>.
- Rilis: ditandai via GitHub Actions; deployment melewati staging lalu produksi setelah QA sign-off.
- Deploy produksi: terjadwal atau sesuai kebutuhan dengan catatan rollout; hanya engineer berizin yang dapat memicu deploy produksi.

Pelaporan insiden
- Gunakan channel insiden (#incidents) di Slack dan ikuti runbook insiden di Confluence.
- Triage: engineer on-call atau Engineering Manager memimpin respons awal; beri tahu CEO untuk insiden berisiko tinggi.

Jam kerja & cuti
- Jam inti: 10:00–16:00 waktu setempat — overlap untuk kerja sinkron.
- Mulai/selesai fleksibel di luar jam inti. Remote/hybrid: kantor tersedia — usahakan hadir minimal satu minggu tatap muka per kuartal jika memungkinkan.
- Ringkasan kebijakan cuti: ajukan lewat HR (tautan portal HR placeholder). Cuti darurat: beri tahu manajer dan HR segera.

---

## 4) Who to ask

- CEO / Founder — Rini — Peran: keputusan strategis, visi perusahaan. Kontak: @rini · rini@nusantarakopi.tech
- CTO / Engineering Manager — Budi / Angga — Peran: arahan teknis dan operasi engineering. Kontak: @budi · budi@nusantarakopi.tech; @angga · angga@nusantarakopi.tech
- HR — Sari — Peran: onboarding, payroll, benefit, kebijakan. Kontak: @sari · sari@nusantarakopi.tech
- Team Lead — Putri — Peran: penugasan sehari-hari, tinjauan kode, mentoring. Kontak: @putri · putri@nusantarakopi.tech
- IT Support — Dedi — Peran: perangkat, akses, VPN, manajer kata sandi. Kontak: @dedi · it@nusantarakopi.tech
- Office Admin — Maya — Peran: perlengkapan kantor, pemesanan ruang rapat. Kontak: @maya · office@nusantarakopi.tech

(Daftar ini adalah placeholder — jika ada kontak nyata, HR akan menyediakan roster tim Anda.)

---

## 5) Glossary

- PR / MR — Pull Request / Merge Request: permintaan untuk menggabungkan kode ke branch. (EN: PR / MR)
- Tinjauan kode — Code review: pemeriksaan perubahan sebelum merge.
- Repo / Repositori — Repositori kode sumber.
- CI / CD — Continuous Integration / Continuous Deployment: build dan deploy otomatis.
- Staging — Lingkungan pra-produksi untuk verifikasi.
- Produksi — Lingkungan live yang melayani pelanggan.
- Sprint — Siklus pengembangan bertimebox (biasanya 2 minggu).
- Standup — Pertemuan sinkron harian yang singkat.
- Retro — Pertemuan retrospektif untuk perbaikan proses tim.
- Isu / Tiket — Item kerja yang dilacak di Jira atau GitHub Issues.
- Branch — Cabang Git untuk fitur atau perbaikan.
- Merge — Menggabungkan perubahan branch ke mainline.
- Runbook — Instruksi langkah demi langkah untuk insiden atau operasi.
- VPN — Virtual Private Network untuk akses internal yang aman.
- Manajer kata sandi — Penyimpanan kredensial bersama (vault perusahaan).

---

## Lampiran: peta istilah bilingual (EN ↔ ID)

Lihat docs/onboarding-term-map.yml untuk daftar mesin. Peta ini memandu terjemahan: pertahankan istilah teknis agar konsisten.

---

Asumsi & catatan
- Pilihan: GitHub + GitHub Actions untuk CI/CD dan Jira untuk pelacakan isu — umum untuk startup ~20 orang.
- Tautan internal adalah placeholder; HR/IT akan memberikan URL nyata setelah provisioning.
