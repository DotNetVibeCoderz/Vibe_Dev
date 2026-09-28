using System.Text;
using System.Text.Json;
using DotCode.Abstractions;

namespace DotCode.Engine.Sessions;

public sealed record SessionSummary(string Id, string Path, DateTimeOffset Created, DateTimeOffset Modified, string? Title, string FirstPrompt, int MessageCount, string? Model, string? GitBranch);

/// <summary>Append-only JSONL transcript per session under <c>~/.dotcode/projects/&lt;project-slug&gt;/&lt;id&gt;.jsonl</c>.
/// Line types: meta, message, title, mode, model.</summary>
public sealed class SessionStore : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();

    public string Id { get; }
    public string FilePath { get; }

    private SessionStore(string id, string path, bool append)
    {
        Id = id;
        FilePath = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public static SessionStore Create(string cwd, string id, string model)
    {
        var path = System.IO.Path.Combine(DotCodePaths.ProjectDataDir(cwd), id + ".jsonl");
        var store = new SessionStore(id, path, append: false);
        store.WriteLine(w =>
        {
            w.WriteString("type", "meta");
            w.WriteString("sessionId", id);
            w.WriteString("cwd", cwd);
            w.WriteString("model", model);
            w.WriteString("version", AppInfo.Version);
            w.WriteString("created", DateTimeOffset.UtcNow.ToString("O"));
            if (Util.ProcessRunner.TryRun("git", "rev-parse --abbrev-ref HEAD", cwd, 1500) is { } branch) w.WriteString("gitBranch", branch);
        });
        return store;
    }

    public static SessionStore OpenForAppend(string path) =>
        new(System.IO.Path.GetFileNameWithoutExtension(path), path, append: true);

    public void AppendMessage(Message message) => WriteLine(w =>
    {
        w.WriteString("type", "message");
        w.WritePropertyName("message");
        JsonSerializer.Serialize(w, message, AbstractionsJsonContext.Default.Message);
    });

    public void AppendTitle(string title) => WriteLine(w => { w.WriteString("type", "title"); w.WriteString("title", title); });
    public void AppendModel(string model) => WriteLine(w => { w.WriteString("type", "model"); w.WriteString("model", model); });

    /// <summary>Records that history was rewound to (and including) a message id.</summary>
    public void AppendRewind(string keepThroughMessageId) => WriteLine(w => { w.WriteString("type", "rewind"); w.WriteString("keepThrough", keepThroughMessageId); });
    public void AppendClear() => WriteLine(w => w.WriteString("type", "clear"));

    private void WriteLine(Action<Utf8JsonWriter> body)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteString("ts", DateTimeOffset.UtcNow.ToString("O"));
            w.WriteEndObject();
        }
        lock (_gate)
        {
            _writer.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
        }
    }

    public void Dispose() => _writer.Dispose();

    public sealed record LoadedSession(string Id, string Cwd, string? Model, string? Title, List<Message> Messages);

    public static LoadedSession Load(string path)
    {
        var messages = new List<Message>();
        string id = System.IO.Path.GetFileNameWithoutExtension(path), cwd = Environment.CurrentDirectory;
        string? model = null, title = null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                switch (root.GetString("type"))
                {
                    case "meta":
                        id = root.GetString("sessionId") ?? id;
                        cwd = root.GetString("cwd") ?? cwd;
                        model = root.GetString("model");
                        break;
                    case "message":
                        if (root.GetProp("message")?.Deserialize(AbstractionsJsonContext.Default.Message) is { } m) messages.Add(m);
                        break;
                    case "title": title = root.GetString("title"); break;
                    case "model": model = root.GetString("model"); break;
                    case "clear": messages.Clear(); break;
                    case "rewind":
                        var keep = root.GetString("keepThrough");
                        var idx = messages.FindIndex(m => m.Id == keep);
                        if (idx >= 0) messages.RemoveRange(idx + 1, messages.Count - idx - 1);
                        else if (keep == "") messages.Clear();
                        break;
                }
            }
            catch (JsonException) { /* tolerate a torn last line */ }
        }
        return new LoadedSession(id, cwd, model, title, messages);
    }

    public static List<SessionSummary> List(string cwd, int limit = 50)
    {
        var dir = DotCodePaths.ProjectDataDir(cwd);
        if (!Directory.Exists(dir)) return [];
        var result = new List<SessionSummary>();
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(limit))
        {
            try { result.Add(Summarize(file)); }
            catch (IOException) { }
        }
        return result.Where(s => s.MessageCount > 0).ToList();
    }

    private static SessionSummary Summarize(FileInfo file)
    {
        string? title = null, model = null, branch = null, first = null;
        var created = file.CreationTimeUtc;
        var count = 0;
        using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                switch (root.GetString("type"))
                {
                    case "meta":
                        model = root.GetString("model");
                        branch = root.GetString("gitBranch");
                        if (DateTimeOffset.TryParse(root.GetString("created"), out var c)) created = c.UtcDateTime;
                        break;
                    case "title": title = root.GetString("title"); break;
                    case "message":
                        count++;
                        if (first is null && root.GetProp("message") is { } m && string.Equals(m.GetString("role"), "user", StringComparison.OrdinalIgnoreCase) && m.GetBool("isMeta") != true &&
                            m.GetProp("content") is { ValueKind: JsonValueKind.Array } content)
                        {
                            foreach (var part in content.EnumerateArray())
                                if (part.GetString("type") == "text" && part.GetString("text") is { Length: > 0 } t && !t.StartsWith('<')) { first = t; break; }
                        }
                        break;
                }
            }
            catch (JsonException) { }
        }
        return new SessionSummary(System.IO.Path.GetFileNameWithoutExtension(file.Name), file.FullName, created, file.LastWriteTimeUtc, title,
            first ?? "(no prompt)", count, model, branch);
    }

    public static string? FindPath(string cwd, string idOrPrefix)
    {
        if (File.Exists(idOrPrefix)) return idOrPrefix;
        var dir = DotCodePaths.ProjectDataDir(cwd);
        if (Directory.Exists(dir))
        {
            var exact = System.IO.Path.Combine(dir, idOrPrefix + ".jsonl");
            if (File.Exists(exact)) return exact;
            var match = Directory.EnumerateFiles(dir, idOrPrefix + "*.jsonl").FirstOrDefault();
            if (match is not null) return match;
        }
        // Search other projects too (resume by id from anywhere).
        if (Directory.Exists(DotCodePaths.ProjectsDir))
            return Directory.EnumerateFiles(DotCodePaths.ProjectsDir, idOrPrefix + "*.jsonl", SearchOption.AllDirectories).FirstOrDefault();
        return null;
    }
}
