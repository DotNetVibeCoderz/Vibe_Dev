# Instalasi

[English](../en/installation.md) · [Bahasa Indonesia](../id/installation.md)

Marbots punya tiga bagian. **Server** selalu dibutuhkan; dua lainnya opsional.

| Bagian | Isinya |
|---|---|
| **Server** (`Marbots.Server`, `marbots-server`) | UI web, API, bot, memori, dan persetujuan. Berjalan sebagai layanan. |
| **CLI** (`marbots`) | Baris perintah untuk server: chat, bot, host, tenant… |
| **Agent host** (`marbots-host`) | Agar bot bisa bekerja di komputer lain (lihat [Komputer](computers.md)) |

Setiap rilis di GitHub (`marbots-v<versi>`) berisi paket untuk setiap platform. Paketnya mandiri, jadi .NET tidak perlu
dipasang, dan semua berkas tercantum di `SHA256SUMS`.

| Platform | Paket | Cara termudah |
|---|---|---|
| Windows x64 / ARM64 | `marbots-<v>-win-x64.zip` / `win-arm64.zip` | `install.ps1` atau Scoop |
| Linux x64 / ARM64 | `marbots-<v>-linux-x64.tar.gz` / `linux-arm64.tar.gz`, `marbots_<v>_amd64.deb` / `arm64.deb` | `.deb` (Debian/Ubuntu) atau `install.sh` |
| macOS Intel / Apple silicon | `marbots-<v>-osx-x64.tar.gz` / `osx-arm64.tar.gz` | `install.sh` |
| Docker (amd64, arm64) | `ghcr.io/dotnetvibecoderz/marbots:<v>` | `docker compose` |

Setelah terpasang, buka **http://localhost:5170**, hubungkan model di **Settings**, lalu ikuti
[Memulai](getting-started.md).

## Windows

Jalankan PowerShell sebagai pengguna biasa (server jalan saat Anda login) atau **sebagai Administrator** (server
menjadi Windows Service "Marbots", data di `%ProgramData%\Marbots`):

```powershell
irm https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.ps1 | iex
```

Dengan opsi (unduh skripnya dulu, atau pakai bentuk scriptblock):

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.ps1))) -Port 8080 -Public
```

| Opsi | Arti |
|---|---|
| `-Component all\|server\|cli\|host` | Yang dipasang (bawaan `all`) |
| `-Version 0.3.1` | Rilis tertentu (bawaan: terbaru) |
| `-Port 5170`, `-Public` | Port; mendengarkan di semua antarmuka dan membuka firewall (admin) |
| `-InstallDir`, `-DataDir` | Folder (bawaan `%LOCALAPPDATA%\Programs\Marbots` / `%LOCALAPPDATA%\Marbots\Server`, atau Program Files / ProgramData sebagai admin) |
| `-NoService` | Hanya memasang; jalankan server sendiri |
| `-Archive <zip>` | Memasang dari paket yang sudah diunduh (offline) |
| `-Uninstall [-Purge]` | Menghapus Marbots (`-Purge` juga menghapus data) |

`marbots` dan `marbots-host` ditambahkan ke PATH. Menjalankan installer lagi akan memperbarui di tempat dan
mempertahankan data. Dua versi terbaru tetap disimpan.

**Scoop**:

```powershell
scoop install https://github.com/DotNetVibeCoderz/Vibe_Dev/releases/latest/download/marbots.json
marbots-server          # menjalankan server di konsol ini
```

Tautan `releases/latest` mengarah ke rilis terbaru di repositori. Jika proyek lain merilis setelah Marbots, pakai URL
berversi `…/releases/download/marbots-v<v>/marbots.json`.

## Linux

**Debian / Ubuntu** (`.deb`, layanan systemd `marbots`, data di `/var/lib/marbots`):

```bash
curl -fLO https://github.com/DotNetVibeCoderz/Vibe_Dev/releases/download/marbots-v0.3.1/marbots_0.3.1_amd64.deb
sudo apt install ./marbots_0.3.1_amd64.deb
sudo nano /etc/marbots/marbots.env      # opsional: alamat listen, kunci API, database…
sudo systemctl restart marbots
```

`apt remove marbots` mempertahankan data; `apt purge marbots` menghapus data beserta pengguna `marbots`.

**Distribusi apa pun** (`install.sh`):

```bash
curl -fsSL https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.sh | bash          # untuk pengguna Anda
curl -fsSL https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.sh | sudo bash     # untuk seluruh sistem
```

- **Untuk pengguna Anda**: dipasang di `~/.local/share/marbots`, perintah di `~/.local/bin`, dan dibuat layanan
  systemd user `marbots` (dengan lingering, sehingga tetap jalan tanpa login).
- **Seluruh sistem (`sudo`)**: dipasang di `/opt/marbots`, perintah di `/usr/local/bin`, dan dibuat layanan systemd
  `marbots` yang berjalan sebagai pengguna `marbots`, dengan data di `/var/lib/marbots` dan pengaturan di
  `/etc/marbots/marbots.env`.

Opsi (setelah `bash -s --`): `--component`, `--version`, `--port`, `--public`, `--prefix`, `--data`, `--no-service`,
`--archive <tar.gz>`, `--uninstall [--purge]`.

## macOS

```bash
curl -fsSL https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.sh | bash
```

Skrip memilih paket Intel atau Apple silicon dan menghapus tanda karantina. Skrip juga menandatangani executable
secara ad hoc, yang diwajibkan Apple silicon. Server berjalan sebagai launchd agent `id.gravicode.marbots` (dengan
`sudo`: LaunchDaemon) dan menulis log ke `<data>/server.log`.

## Docker

```bash
git clone --depth 1 https://github.com/DotNetVibeCoderz/Vibe_Dev && cd Vibe_Dev/Marbots
docker compose -f install/docker/compose.yml up -d                                          # SQLite di volume
docker compose -f install/docker/compose.yml -f install/docker/compose.postgres.yml up -d   # + PostgreSQL
#   compose.sqlserver.yml (SQL Server) atau compose.mysql.yml (MySQL) dipakai dengan cara yang sama
```

- Image-nya `ghcr.io/dotnetvibecoderz/marbots:<versi>` (atau `:latest`), untuk amd64 dan arm64. Compose membangunnya
  dari kode sumber jika Anda menambahkan `--build`.
- Data disimpan di volume `marbots-data` (`/data` di container); container mendengarkan di 8080, yang dipetakan
  compose ke `${MARBOTS_PORT:-5170}`.
- Pengaturan ditaruh di `install/docker/marbots.env` (salin dari `marbots.env.example`): kunci API, provider, mode
  multi-tenant, telemetri, dan sebagainya.
- Ganti kata sandi database bawaan (`POSTGRES_PASSWORD`, `MSSQL_SA_PASSWORD`, `MYSQL_PASSWORD`) sebelum server dibuka
  ke jaringan.

Satu container saja:

```bash
docker run -d --name marbots -p 5170:8080 -v marbots-data:/data ghcr.io/dotnetvibecoderz/marbots:latest
```

## Hanya CLI

CLI ada di setiap paket (`bin/marbots`). CLI juga tersedia sebagai .NET tool:

```bash
dotnet tool install -g Marbots.Cli
export MARBOTS_URL=http://server:5170 MARBOTS_API_KEY=…
marbots status
```

## Menambahkan komputer lain sebagai agent host

Dari halaman **Computers** di server, Anda bisa memasang host lewat SSH dalam satu langkah. Cara lain: buat token
sekali pakai, lalu jalankan installer di komputer itu sebagai pengguna yang akan dipakai bot:

```bash
curl -fsSL https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.sh | bash -s -- \
  --component host --server http://192.168.1.10:5170 --token mbe_xxx
```

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Dev/main/Marbots/install/install.ps1))) -Component host -Server http://192.168.1.10:5170 -Token mbe_xxx
```

Host mendaftar, menerima sertifikat klien mutual TLS, dan berjalan saat login. Di Windows host berjalan di sesi
desktop, sehingga computer use berfungsi; di Linux berupa layanan systemd user, dan di macOS berupa launchd agent.
Server mengunduh binary host untuk pemasangan SSH dan host container dari rilisnya sendiri secara otomatis, diperiksa
terhadap `SHA256SUMS` (pengaturan `HostPackagesUrl`).

## Memperbarui dan menghapus

| Dipasang dengan | Memperbarui | Menghapus |
|---|---|---|
| `install.ps1` | Jalankan lagi (`-Version` untuk rilis tertentu) | `install.ps1 -Uninstall [-Purge]` |
| `install.sh` | Jalankan lagi | `install.sh --uninstall [--purge]` |
| `.deb` | `sudo apt install ./marbots_<baru>_amd64.deb` | `sudo apt remove marbots` / `purge` |
| Scoop | `scoop update marbots` | `scoop uninstall marbots` |
| Docker | `docker compose pull && docker compose up -d` | `docker compose down` (`-v` menghapus data) |

Data (database, workspace, skill, secret, kunci) tidak pernah disentuh saat pembaruan. Cadangkan dengan menyalin
folder data saat server berhenti, atau dengan alat cadangan database Anda bila memakai PostgreSQL, SQL Server, atau
MySQL.

## Yang sudah diuji

- **Windows 11**: pemasangan pengguna dari paket, server lewat task logon, CLI, pembaruan, dan penghapusan (PATH dan
  task terhapus, data dipertahankan).
- **Ubuntu 24.04** (container): `install.sh` sebagai root, server, CLI, dan host; penghapusan dengan purge. Paket
  `.deb` dibangun, dipasang (dependensi terpenuhi), dijalankan sebagai pengguna `marbots`, dihapus (data dipertahankan),
  lalu di-purge.
- **Docker**: image dibangun dan dijalankan dengan overlay PostgreSQL.
- **macOS Intel**: pemasangan host lewat SSH dengan launchd agent.

Belum diuji di mesin sungguhan: jalur Windows Service (perlu pemasangan sebagai admin), unit systemd, dan manifest
Scoop.

---
*Marbots: Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
