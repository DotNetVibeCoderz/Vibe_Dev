# Pengembangan

> 🇬🇧 [English](../en/development.md)

## Prasyarat

.NET 10 SDK (lihat `global.json`), Git. Untuk NativeAOT di Windows: Visual Studio 2022+ "Desktop development with C++" (tambahkan `C:\Program Files (x86)\Microsoft Visual Studio\Installer` ke `PATH` bila `vswhere.exe` tidak ditemukan). Node 18+ (SDK TypeScript), Python 3.9+ (SDK Python), Go 1.22+ (SDK Go).

## Build dan menjalankan

```bash
dotnet build DotCode.slnx
dotnet run --project src/DotCode.Cli -- --model mock:echo      # offline, tanpa API key
```

## Test

```bash
dotnet test tests/DotCode.Tests
dotnet test tests/DotCode.Tests --filter "FullyQualifiedName~ProviderContractTests"
cd sdk/typescript && npm install && npm run build && npm test
cd sdk/python/tests && PYTHONPATH=../src python -m unittest -v
cd sdk/go && go test ./...
```

Suite test mencakup: kontrak semua adapter provider (stream SSE/NDJSON via mock HTTP), aturan izin dan analisis shell, diff, perbaikan JSON, frontmatter, penggabungan settings, loop agen dengan provider skrip (tools, izin, plan mode, edit CRLF, subagent, todo, command/skill, rewind), UI terminal (lebar tampilan, wrap, markdown, tema), serta konformansi SDK di keempat bahasa.

## Publish

```bash
dotnet publish src/DotCode.Cli -c Release -r win-x64 -o artifacts/win-x64          # NativeAOT satu file
dotnet pack src/DotCode.Cli -c Release -p:DotCodeTool=true -o artifacts/nuget      # paket "dotnet tool"
dotnet pack src/DotCode.Sdk -c Release -o artifacts/nuget
cd sdk/typescript && npm pack
cd sdk/python && python -m build
```

## Rilis

Rilis dibuat dengan mendorong tag `dotcode-vX.Y.Z` (repositori ini juga berisi proyek lain, jadi setiap tag DotCode memakai awalan ini). Versi diambil dari tag (`-p:Version`), jadi samakan `<Version>` di `Directory.Build.props` untuk build lokal dan NuGet. Workflow `DotCode` lalu:

1. menjalankan semua test (termasuk test sandbox, LSP, dan installer);
2. mem-build binary NativeAOT di runner native untuk `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64` (Ubuntu 22.04 → glibc 2.35+), `osx-arm64`, dan `osx-x64` (cross-compile, diuji lewat Rosetta), lalu menguji singkat masing-masing (`--version`, prompt dengan model mock);
3. mengemasnya menjadi `dotcode-<rid>.zip` (Windows) / `.tar.gz` (Unix, bit executable terjaga), menulis `SHA256SUMS`, SBOM SPDX, dan attestation build-provenance GitHub;
4. mem-pack dan mendorong paket NuGet, lalu membuat rilis GitHub berisi arsip, checksum, SBOM, installer, dan `.nupkg`.

Untuk mencoba seluruh pipeline tanpa mempublikasikan apa pun, jalankan workflow secara manual dengan **build_binaries** (`gh workflow run dotcode.yml -f build_binaries=true`); hasilnya berupa artifact `release-assets`. Paket SDK dirilis terpisah (npm, PyPI, crates.io secara manual; Go dan Java lewat tag masing-masing, lihat CLAUDE.md). Setelah rilis, perbarui `packaging/scoop/dotcode.json` (versi, URL, hash dari `SHA256SUMS`).

Installer (`install/install.sh`, `install/install.ps1`) dan `dotcode update` membaca rilis dari GitHub API. `DOTCODE_RELEASES_API` / `DOTCODE_DOWNLOAD_BASE` mengarahkannya ke mirror atau server uji. `install/test-install.sh` menjalankan `install.sh` terhadap rilis palsu lokal: ia memeriksa instalasi terverifikasi dan bahwa arsip yang diubah ditolak.

## Screenshot

`tools/DotCode.TermCapture` menjalankan sesi berskrip di pseudo console Windows, mengemulasi terminal, dan merender HTML/PNG. Skenario ada di `tools/DotCode.TermCapture/scenarios/*.json` — semua screenshot di dokumentasi ini dibuat dengan LLM sungguhan (Azure OpenAI gpt-5-mini dan DeepSeek).

## Konvensi

- Proyek inti harus bersih AOT: tidak ada JSON berbasis refleksi.
- Tool baru diturunkan dari `Tool` dan didaftarkan di `BuiltinToolset`.
- Provider baru mengimplementasikan `IModelProvider` dan wajib punya contract test.
- Perilaku yang terlihat pengguna disamakan dengan Claude Code bila konsepnya ada di keduanya.
