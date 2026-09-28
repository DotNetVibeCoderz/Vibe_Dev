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

## Screenshot

`tools/DotCode.TermCapture` menjalankan sesi berskrip di pseudo console Windows, mengemulasi terminal, dan merender HTML/PNG. Skenario ada di `tools/DotCode.TermCapture/scenarios/*.json` — semua screenshot di dokumentasi ini dibuat dengan LLM sungguhan (Azure OpenAI gpt-5-mini dan DeepSeek).

## Konvensi

- Proyek inti harus bersih AOT: tidak ada JSON berbasis refleksi.
- Tool baru diturunkan dari `Tool` dan didaftarkan di `BuiltinToolset`.
- Provider baru mengimplementasikan `IModelProvider` dan wajib punya contract test.
- Perilaku yang terlihat pengguna disamakan dengan Claude Code bila konsepnya ada di keduanya.
