using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Agent;
using DotCode.Engine.Configuration;
using DotCode.Engine.Lsp;
using DotCode.Engine.Util;

namespace DotCode.Tests;

public sealed class LspTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-lsp-" + Guid.NewGuid().ToString("n")[..8]);

    public LspTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("DOTCODE_CONFIG_DIR", Path.Combine(_dir, ".cfg"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Client_speaks_framed_json_rpc_and_collects_diagnostics()
    {
        var file = Path.Combine(_dir, "a.ts");
        File.WriteAllText(file, "const x: string = 1;\n");
        await using var server = new FakeLspServer(_dir, file);
        await using var client = new LspClient("fake", _dir, server.ToClient, server.FromClient);
        await client.InitializeAsync(null, CancellationToken.None);

        Assert.True(client.Supports("definitionProvider"));
        Assert.False(client.Supports("diagnosticProvider"));
        Assert.Equal("1", server.ConfigurationAnswer);                  // server → client request was answered

        await client.SyncDocumentAsync(file, "typescript", CancellationToken.None);
        var diagnostics = await client.GetDiagnosticsAsync(file, 0, TimeSpan.FromSeconds(5), CancellationToken.None);
        var d = Assert.Single(diagnostics);
        Assert.Equal((0, 6, 1), (d.Line, d.Character, d.Severity));
        Assert.Contains("not assignable", d.Message);

        // Unchanged content is not re-sent; changed content is (didChange, version 2).
        await client.SyncDocumentAsync(file, "typescript", CancellationToken.None);
        File.WriteAllText(file, "const x: number = 1;\n");
        await client.SyncDocumentAsync(file, "typescript", CancellationToken.None);
        await server.WaitForAsync("textDocument/didChange");
        Assert.Equal(1, server.Count("textDocument/didOpen"));
        Assert.Equal(1, server.Count("textDocument/didChange"));

        var definition = await client.RequestAsync("textDocument/definition", w => w.WriteString("probe", "x"), CancellationToken.None);
        Assert.Equal("file", new Uri(definition[0].GetString("targetUri")!).Scheme);
    }

    [Fact]
    public async Task Lsp_tool_formats_locations_hover_symbols_and_diagnostics()
    {
        var file = Path.Combine(_dir, "a.ts");
        File.WriteAllText(file, "const x: string = 1;\nexport function add(a: number) { return a; }\n");
        await using var server = new FakeLspServer(_dir, file);
        var client = new LspClient("fake", _dir, server.ToClient, server.FromClient);
        await client.InitializeAsync(null, CancellationToken.None);

        var def = new LspServerDef("fake", "fake", [], [".ts"], [], "typescript", null, null, true);
        await client.SyncDocumentAsync(file, "typescript", CancellationToken.None);
        var diagnostics = await client.GetDiagnosticsAsync(file, 0, TimeSpan.FromSeconds(5), CancellationToken.None);
        var text = Engine.Tools.Builtin.LspTool.FormatDiagnostics(file, diagnostics, _dir, 10);
        Assert.Equal("a.ts:1:7 [error] Type 'number' is not assignable to type 'string'. (ts 2322)", text);
        Assert.Equal("typescript", LspManager.LanguageId(file, def));
        await client.DisposeAsync();
        Assert.True(server.Count("shutdown") == 1);
    }

    [Fact]
    public void Manager_picks_servers_by_extension_and_finds_the_workspace_root()
    {
        var sub = Path.Combine(_dir, "packages", "web", "src");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_dir, "packages", "web", "tsconfig.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "app.csproj"), "<Project/>");
        var fakeExe = Path.Combine(_dir, OperatingSystem.IsWindows() ? "my-ls.exe" : "my-ls");
        File.WriteAllText(fakeExe, "");
        var settings = new Settings
        {
            Lsp = new LspSettings
            {
                Servers = new()
                {
                    ["cpp"] = new LspServerConfig { Disabled = true },
                    ["mylang"] = new LspServerConfig { Command = fakeExe, Extensions = ["mine"], RootMarkers = ["*.csproj"] },
                },
            },
        };
        var manager = new LspManager(settings, _dir);
        Assert.Equal("typescript", manager.ServerFor(Path.Combine(sub, "x.tsx"))!.Name);
        Assert.Equal(Path.Combine(_dir, "packages", "web"), manager.RootFor(Path.Combine(sub, "x.ts"), manager.ServerFor(Path.Combine(sub, "x.ts"))!));
        Assert.Null(manager.ServerFor("x.cpp"));                              // disabled built-in
        var mine = manager.ServerFor(Path.Combine(sub, "y.mine"))!;
        Assert.Equal("mylang", mine.Name);
        Assert.True(mine.Available);
        Assert.Equal(_dir, manager.RootFor(Path.Combine(sub, "y.mine"), mine)); // *.csproj glob marker
        Assert.False(new LspManager(new Settings { Lsp = new LspSettings { Enabled = false } }, _dir).AnyAvailable);

        var path = Path.Combine(_dir, "dir with space", "ä.ts");
        Assert.Equal(Path.GetFullPath(path), LspClient.UriToPath(LspClient.PathToUri(path)));
        if (OperatingSystem.IsWindows())
            Assert.Equal(@"C:\x\y.ts", LspClient.UriToPath("file:///c%3A/x/y.ts"), ignoreCase: true);   // tsserver-style URI
    }

    [Fact]
    public async Task Real_typescript_language_server_answers_the_lsp_tool()
    {
        // Runs where typescript-language-server (with TypeScript 5) is installed; CI installs it and sets
        // DOTCODE_REQUIRE_LSP so the test cannot silently skip there.
        if (ProcessRunner.FindOnPath("typescript-language-server") is null)
        {
            Assert.False(Environment.GetEnvironmentVariable("DOTCODE_REQUIRE_LSP") == "1", "typescript-language-server is required but not on PATH");
            return;
        }
        File.WriteAllText(Path.Combine(_dir, "tsconfig.json"), """{"compilerOptions":{"strict":true,"module":"es2022","moduleResolution":"bundler","target":"es2022"},"include":["*.ts"]}""");
        File.WriteAllText(Path.Combine(_dir, "math.ts"), "export function add(a: number, b: number): number {\n  return a + b;\n}\n");
        File.WriteAllText(Path.Combine(_dir, "app.ts"), "import { add } from \"./math\";\nconst s = add(1, 2);\nconst bad: string = add(3, 4);\nconsole.log(s, bad);\n");
        var scriptPath = Path.Combine(_dir, "script.json");
        File.WriteAllText(scriptPath, """
            {"responses":[
              {"toolCalls":[{"name":"LSP","input":{"operation":"goToDefinition","file_path":"app.ts","line":2,"character":11}}]},
              {"toolCalls":[{"name":"LSP","input":{"operation":"findReferences","file_path":"math.ts","line":1,"character":17}}]},
              {"toolCalls":[{"name":"LSP","input":{"operation":"diagnostics","file_path":"app.ts"}}]},
              {"text":"done"}
            ]}
            """);
        var settings = $$$"""{"providers":{"mock":{"type":"mock","script":{{{JsonSerializer.Serialize(scriptPath, TestJson.Default.String)}}}}},"autoCompact":false}""";
        await using var runtime = AgentRuntime.Create(new RuntimeOptions { Cwd = _dir, Model = "mock:scripted", SettingsJson = settings, NoMcp = true, PersistSession = false, DangerouslySkipPermissions = true });
        var session = runtime.CreateSession(persist: false);
        await session.RunTurnAsync("inspect");
        var results = session.Messages.SelectMany(m => m.ToolResults).Select(r => r.TextContent).ToList();
        Assert.Equal(3, results.Count);
        Assert.StartsWith("math.ts:1:17", results[0]);
        Assert.Contains("Found 4 references in 2 files", results[1]);
        Assert.Contains("app.ts:3:7 [error] Type 'number' is not assignable to type 'string'.", results[2]);
    }

    /// <summary>Scripted language server over in-process pipes.</summary>
    private sealed class FakeLspServer : IAsyncDisposable
    {
        private readonly AnonymousPipeServerStream _toClientServer = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _fromClientServer = new(PipeDirection.In);
        private readonly List<string> _methods = [];
        private readonly string _root;
        private readonly string _file;
        private readonly Task _loop;

        public Stream ToClient { get; }
        public Stream FromClient { get; }
        public string? ConfigurationAnswer { get; private set; }

        public FakeLspServer(string root, string file)
        {
            _root = root;
            _file = file;
            ToClient = new AnonymousPipeClientStream(PipeDirection.In, _toClientServer.ClientSafePipeHandle);
            FromClient = new AnonymousPipeClientStream(PipeDirection.Out, _fromClientServer.ClientSafePipeHandle);
            _loop = Task.Run(LoopAsync);
        }

        public int Count(string method) { lock (_methods) return _methods.Count(m => m == method); }

        public async Task WaitForAsync(string method)
        {
            for (var i = 0; i < 100 && Count(method) == 0; i++) await Task.Delay(20);
        }

        private async Task SendAsync(string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await _toClientServer.WriteAsync(header);
            await _toClientServer.WriteAsync(body);
            await _toClientServer.FlushAsync();
        }

        private async Task<JsonElement?> ReadAsync()
        {
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await _fromClientServer.ReadAsync(one) == 0) return null;
                header.Append((char)one[0]);
            }
            var length = int.Parse(header.ToString().Split(':')[1].Trim());
            var body = new byte[length];
            var read = 0;
            while (read < length) read += await _fromClientServer.ReadAsync(body.AsMemory(read));
            return DotCodeJson.Parse(Encoding.UTF8.GetString(body));
        }

        private async Task LoopAsync()
        {
            var uri = LspClient.PathToUri(_file);
            while (await ReadAsync() is { } msg)
            {
                var method = msg.GetString("method");
                var id = msg.GetProp("id")?.GetRawText();
                if (method is null)
                {
                    // Our response to the workspace/configuration request.
                    if (msg.GetProp("result") is { ValueKind: JsonValueKind.Array } arr) ConfigurationAnswer = arr.GetArrayLength().ToString();
                    continue;
                }
                lock (_methods) _methods.Add(method);
                switch (method)
                {
                    case "initialize":
                        await SendAsync("""{"jsonrpc":"2.0","id":900,"method":"workspace/configuration","params":{"items":[{"section":"ts"}]}}""");
                        await SendAsync("""{"jsonrpc":"2.0","id":""" + id + ""","result":{"capabilities":{"definitionProvider":true,"referencesProvider":true,"hoverProvider":true,"textDocumentSync":1}}}""");
                        break;
                    case "textDocument/didOpen":
                        await SendAsync($$$"""{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"{{{uri}}}","diagnostics":[{"range":{"start":{"line":0,"character":6},"end":{"line":0,"character":7}},"severity":1,"code":2322,"source":"ts","message":"Type 'number' is not assignable to type 'string'."}]}}""");
                        break;
                    case "textDocument/definition":
                        await SendAsync("""{"jsonrpc":"2.0","id":""" + id + ""","result":[{"targetUri":""" + JsonSerializer.Serialize(uri, TestJson.Default.String)
                            + ""","targetRange":{"start":{"line":1,"character":0},"end":{"line":1,"character":40}},"targetSelectionRange":{"start":{"line":1,"character":16},"end":{"line":1,"character":19}}}]}""");
                        break;
                    case "shutdown":
                        await SendAsync($$"""{"jsonrpc":"2.0","id":{{id}},"result":null}""");
                        break;
                    default:
                        if (id is not null) await SendAsync($$"""{"jsonrpc":"2.0","id":{{id}},"result":null}""");
                        break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _toClientServer.DisposeAsync();
            await _fromClientServer.DisposeAsync();
            await ToClient.DisposeAsync();
            await FromClient.DisposeAsync();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}
