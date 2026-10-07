# Komputer: menjalankan bot di mesin lain

[English](../en/computers.md) · [Bahasa Indonesia](../id/computers.md)

Server Marbots dapat mempekerjakan bot di komputer lain: PC build, workstation desain dengan layar besar, mesin
Linux dengan Docker, atau VM. Server tetap memegang hal yang harus terpusat: percakapan, model dan API key, kebijakan,
persetujuan, memori, dan pencarian web. Komputer lain menjalankan tool lingkungan bot di workspace miliknya sendiri.

| Berjalan di komputer bot | Tetap di server |
|---|---|
| `files` (baca/tulis/ubah/daftar/hapus), `search` (grep) | loop agen dan panggilan model |
| `shell` (`run_shell`, `install_package`) | persetujuan dan kebijakan izin |
| `desktop` (screenshot, mouse, keyboard) | memori, todo, pencarian/ambil web, server MCP |
| Container Docker untuk shell bot (opsional) | skill (berkasnya disalin saat dimuat) |

![Halaman Komputer](../images/hosts.png)

## Menambah komputer lewat SSH

**Komputer → Install over SSH** di aplikasi web, atau lewat CLI:

```bash
marbots hosts bootstrap devuser@192.168.1.20 --server http://192.168.1.10:5170 --name DEV2 --password-file pw.txt
# atau MARBOTS_SSH_PASSWORD=… / --key-file id_ed25519
```

Yang terjadi:

1. **Preflight.** Marbots terhubung sekali, mendeteksi OS dan arsitektur CPU, lalu memastikan komputer bisa menjangkau
   server di `--server`. Pakai alamat LAN server, bukan `localhost`.
2. **Unggah.** Biner `marbots-host` self-contained (target tidak perlu .NET) disalin dari
   `data/host-packages/marbots-host-<rid>[.exe]`.
3. **Enroll.** Token sekali pakai (berlaku 10 menit, disimpan hanya sebagai hash) ditukar dengan id dan secret host.
   Di Windows, secret dilindungi DPAPI untuk pengguna tersebut.
4. **Jalan saat logon.** Di Windows berupa scheduled task di sesi desktop pengguna, sehingga tool computer-use bisa
   melihat layar. Di Linux berupa systemd user service.
5. **Online.** Host terhubung keluar ke server (aman di balik NAT) dan melaporkan kemampuannya.

Password atau key SSH hanya dipakai untuk permintaan itu dan tidak pernah disimpan. Untuk platform lain, buat paket
dengan `dotnet publish src/Marbots.AgentHost -c Release -r linux-x64 -o data/host-packages`, lalu ganti nama berkasnya
menjadi `marbots-host-linux-x64`.

**Pembaruan.** `marbots hosts bootstrap … --update` mengganti biner dan me-restart host tanpa mengubah pendaftarannya.

### Instal manual

**Komputer → Install by hand → Create token**, atau `marbots hosts token DEV2`, lalu di komputer tersebut:

```bash
marbots-host enroll --server http://192.168.1.10:5170 --token mbe_… --name DEV2
marbots-host run
```

## Menempatkan bot di komputer

| Cara | Bagaimana |
|---|---|
| Web | Editor bot → **Host** (tiap komputer menampilkan status dan kemampuannya). Opsional: **Container image**. |
| CLI | `marbots bot host nova host-dev2-70df`; `marbots bot container dockie python:3.12-slim --cpus 1 --memory 1024` |
| SDK | `new BotDefinition { HostRef = host.Id, Container = new ContainerProfile { Image = "python:3.12-slim" } }` |
| Boss Man | "Buat data engineer di DEV2 yang menjalankan shell di python:3.12-slim". Boss Man memakai `list_hosts` dan `create_bot` dengan `host` dan `container_image`. |

`HostRef: "auto"` membiarkan **placement** memilih. Komputer yang tidak punya kemampuan yang dibutuhkan bot (`shell`,
`desktop`, `docker`) dilewati. Sisanya dinilai dari CPU, memori bebas, dan jumlah panggilan yang sedang berjalan.
Sebuah utas tetap berada di komputer yang menyimpan workspace-nya.

Berkas buatan bot jarak jauh muncul di daftar **Files** pada chat ("on DEV2") dan diunduh melalui server.

## Konsol host

`marbots-host run` menampilkan siapa yang sedang bekerja di komputer itu, panggilan tool terakhir beserta durasinya,
dan beban mesin. Saat bot memakai mouse dan keyboard, banner yang jelas meminta orang di depan komputer untuk tidak
mengetik. Jika output dialihkan, konsol beralih ke baris log biasa (juga ditulis ke `host.log`).

## Prasyarat: install_package

Bot memasang kebutuhan tugas di komputernya dengan package manager setempat:

| Platform | Manager |
|---|---|
| Windows | winget (per pengguna dulu), scoop, choco |
| Linux | apt, dnf, yum, pacman, apk, zypper (dengan `sudo -n` bila bukan root) |
| macOS | brew |
| Semua | `pip:<paket>`, `npm:<paket>`, `dotnet-tool:<tool>` |

Nama umum dipetakan ke id paket yang benar: `python`, `node`, `git`, `java`, `go`, `rust`, `ffmpeg`, `pandoc`,
`libreoffice`, `docker`. .NET SDK memakai skrip `dotnet-install` resmi Microsoft ke profil pengguna, sehingga tidak
perlu hak administrator. Sebelum memasang, tool memeriksa apakah program sudah ada. Setelahnya, setiap panggilan
`run_shell` membaca PATH terbaru, sehingga tool baru langsung bisa dipakai. `install_package` meminta persetujuan
seperti perintah shell lain, kecuali profil bot `autonomous`.

## Container

Dengan profil container, setiap panggilan `run_shell` berjalan di container `docker run --rm` sekali pakai. Container
mendapat kuota CPU dan memori, label `marbots.bot`, dan hanya workspace yang di-mount (di `/workspace`). Akses jaringan
opsional. Bot diberi tahu bahwa ia berada di dalam container dan harus memakai `sh`.

## Computer use

Pack `desktop` memberi bot empat tool:

- `screenshot`: model melihat gambarnya, dan gambar disimpan di `screenshots/`;
- `mouse_click`: koordinat dalam piksel screenshot;
- `type_text`;
- `press_keys`.

Tool ini butuh komputer Windows dengan pengguna yang sedang login, karena host berjalan di sesi desktop tersebut.
Klik dan ketik termasuk aksi `ProcessExecution`, sehingga meminta persetujuan pada profil `developer-safe`.

## Keandalan

- Host tersambung ulang sendiri dengan back-off.
- Panggilan tool yang sedang berjalan saat koneksi putus menunggu host kembali hingga 60 detik, lalu dikirim ulang
  dengan request id yang sama. Host menjawab dari cache hasil, sehingga tool tidak dijalankan dua kali.
- Host bisa dinonaktifkan atau dihapus di halaman Komputer atau dengan `marbots hosts disable|remove <id>`.

## Protokol

Tiap host memakai satu WebSocket di `/api/v1/hosts/connect`, diautentikasi dengan `X-Marbots-Host` dan
`X-Marbots-Host-Secret`. Isinya frame JSON: `hello`, `heartbeat`, `invoke`/`result`, `list-files`, `read-file`,
`put-files`, dan `cancel`. WebSocket di port yang sudah ada menggantikan ide gRPC pada desain awal karena tiga alasan:
host membuka koneksi keluar, cukup satu port, dan JSON-nya source-generated (lihat ADR di `PLAN.md`).

---
*Marbots — Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*
