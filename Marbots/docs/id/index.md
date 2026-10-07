# Dokumentasi Marbots

[English](../en/index.md) · [Bahasa Indonesia](../id/index.md)

Marbots (Marvelous Bots) adalah platform kolaborasi multi-agen di atas .NET 10. Anda memberi tujuan kepada
**Boss Man**, yang merencanakan pekerjaan lalu membagikannya ke tim rekan kerja AI. Setiap rekan kerja punya persona,
memori, skill, tool MCP, dan batasan izin masing-masing.

| Mulai dari sini | Lalu |
|---|---|
| [Instalasi](installation.md) | Windows (skrip, Service, Scoop), Linux (.deb, skrip, systemd), macOS (launchd), Docker/compose, agent host, pembaruan |
| [Memulai](getting-started.md) | Instalasi, menghubungkan model, tugas multi-bot pertama |
| [Konsep inti](concepts.md) | Bot, Boss Man, utas, tugas, memori, pemadatan, auto-learn |
| [Bot dan templat](bots-and-templates.md) | Galeri 58 templat, formulir bot, profil izin, ekspor/impor `.marbot` |
| [Skill](skills.md) | Paket SKILL.md, pengungkapan bertahap, skill hasil auto-learn, evaluasi pembelajaran dan rollback |
| [Server MCP](mcp.md) | Galeri, server terikat workspace, server stdio/HTTP kustom |
| [Keamanan](security.md) | Mesin kebijakan, persetujuan, secret, prompt injection, API key |
| [Jadwal](scheduling.md) | Job cron dan sekali jalan |
| [Komputer](computers.md) | Menjalankan bot di mesin lain: bootstrap SSH (Windows, Linux, macOS), agent host, placement GPU, host container, mutual TLS, install_package, computer use |
| [Aplikasi desktop dan mobile](apps.md) | Aplikasi Avalonia dengan kantor 3D (aset Rodin + Blender), aplikasi mobile MAUI dengan notifikasi |
| [Kanal dan trigger](channels-and-triggers.md) | Web chat, webhook, Telegram, Slack, WhatsApp, Discord, e-mail; trigger webhook dan event; mode suggest; streaming A2A |
| [Multi-tenant, database, dan masuk](multi-tenant.md) | Tenant, SQLite/PostgreSQL/SQL Server/MySQL, memori hybrid, kunci API, OIDC, peran |
| [Operasional](operations.md) | OpenTelemetry, notifikasi push (FCM/APNs/ntfy), rilis dan SBOM, performa |
| [API, A2A, SDK, dan CLI](api-and-sdks.md) | REST + SSE, Agent2Agent, SDK .NET/Python/TypeScript/Go, CLI `marbots` |
| [Arsitektur](architecture.md) | Modul, alur permintaan, performa, konfigurasi |
| [Uji coba](trials.md) | Apa yang dibangun para bot dengan LLM sungguhan, lengkap dengan tangkapan layar |
| [Pemecahan masalah](troubleshooting.md) | Masalah umum dan solusinya |
| [Glosarium](../glossary.md) | Istilah EN ↔ ID |

---
*Marbots — Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
