using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marbots.Abstractions;
using Marbots.Kernel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Marbots.Runtime;

/// <summary>
/// Optional per-bot learning loop. Extracts durable facts into memory and, in SuggestSkills mode, drafts reusable
/// skills into a pending area that requires human approval. Learned output never grants new privileges.
/// </summary>
public sealed class AutoLearnService(IModelRouter router, IMessageStore messages, IMemoryStore memory, SkillRegistry skills, IEventBus bus, ILogger<AutoLearnService> log)
{
    public async Task LearnAsync(BotDefinition bot, TaskRecord task, CancellationToken ct)
    {
        if (bot.AutoLearn == AutoLearnMode.Off || !bot.LongTermMemory) return;
        try
        {
            var transcript = new StringBuilder();
            if (task.Depth == 0) transcript.Append("user: ").AppendLine(task.Objective.Length > 800 ? task.Objective[..800] : task.Objective);
            foreach (var m in await messages.ListAsync(task.TranscriptId, 0, 400, ct))
            {
                if (m.TaskId != task.Id && task.Depth == 0) continue;
                var text = m.Content.Length > 800 ? m.Content[..800] + "…" : m.Content;
                transcript.Append(m.Role == "tool" ? $"tool {m.ToolName}" : m.Role).Append(": ").AppendLine(text);
            }
            if (transcript.Length < 80) return;
            var wantSkill = bot.AutoLearn == AutoLearnMode.SuggestSkills && task.Steps >= 3;
            var prompt = $$"""
                Review this completed task by {{bot.Name}} and extract what is worth remembering long-term.
                Return ONLY JSON: {"memories":[{"content":"standalone fact or user preference","kind":"semantic|procedural|episodic"}]{{(wantSkill ? ",\"skill\":null | {\"name\":\"kebab-case\",\"description\":\"one line\",\"body\":\"markdown step-by-step procedure\"}" : "")}}}
                Rules: at most 3 memories; skip trivia and anything task-specific that won't matter later; never include secrets, keys, passwords or personal data.
                {{(wantSkill ? "Only propose a skill if the task revealed a reusable multi-step procedure." : "")}}

                Transcript:
                {{(transcript.Length > 14000 ? transcript.ToString(0, 14000) : transcript.ToString())}}
                """;
            var (resp, _) = await router.CompleteAsync(bot.ModelProfile, new ModelRequest { Messages = [ModelMessage.User(prompt)], MaxOutputTokens = 3000 }, ct);
            var json = ExtractJson(resp.Content);
            if (json is null) return;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("memories", out var mems) && mems.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in mems.EnumerateArray().Take(3))
                {
                    var content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                    if (content.Length is < 8 or > 500 || SecretScanner.LooksSensitive(content)) continue;
                    var near = await memory.SearchAsync(new MemoryQuery(content, [bot.Id], 3), ct);
                    if (near.Any(n => Similarity(n.Record.Content, content) >= 0.6)) continue;
                    var kind = Enum.TryParse<MemoryKind>(m.TryGetProperty("kind", out var k) ? k.GetString() : null, true, out var mk) ? mk : MemoryKind.Semantic;
                    await memory.WriteAsync(new MemoryRecord { Owner = bot.Id, Kind = kind, Content = content, Source = $"autolearn:{task.Id}", Confidence = 0.6, Tags = ["auto-learn"] }, ct);
                    await bus.PublishAsync(new AgentEvent { Type = EventTypes.MemoryWritten, BotId = bot.Id, TaskId = task.Id, ThreadId = task.ThreadId, Message = "Auto-learn: " + content }, ct);
                }
            }
            if (wantSkill && doc.RootElement.TryGetProperty("skill", out var sk) && sk.ValueKind == JsonValueKind.Object)
            {
                var name = sk.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var desc = sk.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var body = sk.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                if (name.Length > 0 && body.Length > 40 && !SecretScanner.LooksSensitive(body))
                {
                    var info = await skills.CreateAsync(name, desc, $"<!-- Drafted by auto-learn from task {task.Id} ({bot.Name}). Review before approving. -->\n\n{body}", pending: true, ct, author: bot.Id);
                    await bus.PublishAsync(new AgentEvent { Type = EventTypes.AutoLearnCandidateCreated, BotId = bot.Id, TaskId = task.Id, ThreadId = task.ThreadId, Message = $"Skill candidate '{info.Name}' awaits review" }, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogDebug(ex, "Auto-learn skipped for task {Task}", task.Id);
        }
    }

    /// <summary>Word-set Jaccard similarity, used to skip near-duplicate memories.</summary>
    public static double Similarity(string a, string b)
    {
        static HashSet<string> Words(string s) => s.ToLowerInvariant()
            .Split([' ', ',', '.', ';', ':', '"', '\'', '(', ')', '-', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).ToHashSet();
        var x = Words(a);
        var y = Words(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        var inter = x.Count(y.Contains);
        return (double)inter / (x.Count + y.Count - inter);
    }

    public static string? ExtractJson(string? text)
    {
        if (text is null) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}

/// <summary>Five-field cron expressions: minute hour day-of-month month day-of-week. Supports *, lists, ranges and steps.</summary>
public sealed class CronExpression
{
    private readonly bool[] _minutes = new bool[60], _hours = new bool[24], _days = new bool[32], _months = new bool[13], _weekdays = new bool[7];
    private readonly bool _dayStar, _weekdayStar;

    private CronExpression(string expr)
    {
        var parts = expr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) throw new ArgumentException("Cron expression must have 5 fields: minute hour day month weekday.");
        Fill(parts[0], 0, 59, _minutes);
        Fill(parts[1], 0, 23, _hours);
        Fill(parts[2], 1, 31, _days);
        Fill(parts[3], 1, 12, _months);
        var wd = new bool[8];
        Fill(parts[4], 0, 7, wd);
        for (var i = 0; i < 7; i++) _weekdays[i] = wd[i];
        if (wd[7]) _weekdays[0] = true; // 7 == Sunday
        _dayStar = parts[2] == "*";
        _weekdayStar = parts[4] == "*";
    }

    public static CronExpression Parse(string expr) => new(expr);

    public static bool TryParse(string expr, out CronExpression? cron)
    {
        try { cron = new CronExpression(expr); return true; }
        catch (ArgumentException) { cron = null; return false; }
    }

    private static void Fill(string field, int min, int max, bool[] target)
    {
        foreach (var part in field.Split(','))
        {
            var step = 1;
            var range = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], out step) || step <= 0) throw new ArgumentException($"Invalid step in '{part}'.");
                range = part[..slash];
            }
            int lo, hi;
            if (range == "*") { lo = min; hi = max; }
            else if (range.Contains('-'))
            {
                var r = range.Split('-');
                if (!int.TryParse(r[0], out lo) || !int.TryParse(r[1], out hi)) throw new ArgumentException($"Invalid range '{range}'.");
            }
            else
            {
                if (!int.TryParse(range, out lo)) throw new ArgumentException($"Invalid value '{range}'.");
                hi = slash >= 0 ? max : lo;
            }
            if (lo < min || hi > max || lo > hi) throw new ArgumentException($"Value out of range in '{part}' (allowed {min}-{max}).");
            for (var v = lo; v <= hi; v += step) target[v] = true;
        }
    }

    /// <summary>Next occurrence strictly after <paramref name="after"/> in the given time zone.</summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(after, tz);
        var t = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var limit = t.AddYears(5);
        while (t < limit)
        {
            if (!_months[t.Month]) { t = new DateTime(t.Year, t.Month, 1).AddMonths(1); continue; }
            var dayOk = _dayStar && _weekdayStar ? true
                : _dayStar ? _weekdays[(int)t.DayOfWeek]
                : _weekdayStar ? _days[t.Day]
                : _days[t.Day] || _weekdays[(int)t.DayOfWeek];
            if (!dayOk) { t = t.Date.AddDays(1); continue; }
            if (!_hours[t.Hour]) { t = t.Date.AddHours(t.Hour + 1); continue; }
            if (!_minutes[t.Minute]) { t = t.AddMinutes(1); continue; }
            if (tz.IsInvalidTime(t)) { t = t.AddMinutes(1); continue; }
            return new DateTimeOffset(t, tz.GetUtcOffset(t));
        }
        return null;
    }
}

/// <summary>Persistent scheduler: cron and one-shot jobs that send a prompt to a bot. Misfires run once on startup.</summary>
public sealed class SchedulerService(IDocumentStore<ScheduleJob> store, IServiceProvider services, IEventBus bus, ILogger<SchedulerService> log) : BackgroundService
{
    private MarbotsEngine Engine => (MarbotsEngine)services.GetService(typeof(MarbotsEngine))!;

    public static TimeZoneInfo Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? "UTC" : id); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException($"Unknown time zone '{id}'."); }
    }

    public static DateTimeOffset? ComputeNext(ScheduleJob job, DateTimeOffset after)
    {
        if (!string.IsNullOrWhiteSpace(job.Cron)) return CronExpression.Parse(job.Cron).Next(after, Zone(job.TimeZone));
        return job.RunAt is { } at && at > after ? at : null;
    }

    public async Task<ScheduleJob> SaveAsync(ScheduleJob job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.Name)) throw new ArgumentException("Name is required.");
        if (string.IsNullOrWhiteSpace(job.Prompt)) throw new ArgumentException("Prompt is required.");
        if (string.IsNullOrWhiteSpace(job.Cron) && job.RunAt is null) throw new ArgumentException("Provide a cron expression or a run_at time.");
        if (!string.IsNullOrWhiteSpace(job.Cron) && !CronExpression.TryParse(job.Cron, out _)) throw new ArgumentException($"Invalid cron expression '{job.Cron}'.");
        Zone(job.TimeZone);
        if (string.IsNullOrEmpty(job.Id)) job.Id = Ids.New("job");
        job.NextRunAt = job.Enabled ? ComputeNext(job, DateTimeOffset.UtcNow) : null;
        await store.UpsertAsync(job, ct);
        return job;
    }

    public Task<IReadOnlyList<ScheduleJob>> ListAsync(CancellationToken ct = default) => store.ListAsync(ct);

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => store.DeleteAsync(id, ct);

    public async Task<TaskRecord> RunNowAsync(string id, CancellationToken ct)
    {
        var job = await store.GetAsync(id, ct) ?? throw new ArgumentException("Schedule not found.");
        return await FireAsync(job, ct);
    }

    private async Task<TaskRecord> FireAsync(ScheduleJob job, CancellationToken ct)
    {
        var engine = Engine;
        if (job.ThreadId is null || await engine.GetThreadAsync(job.ThreadId, ct) is null)
        {
            var thread = await engine.CreateThreadAsync(job.BotId, $"⏰ {job.Name}", ct);
            job.ThreadId = thread.Id;
        }
        var task = await engine.SendAsync(job.ThreadId, job.Prompt, ct);
        job.LastRunAt = DateTimeOffset.UtcNow;
        job.LastTaskId = task.Id;
        job.RunCount++;
        job.NextRunAt = ComputeNext(job, DateTimeOffset.UtcNow);
        if (job.NextRunAt is null && string.IsNullOrWhiteSpace(job.Cron)) job.Enabled = false;
        await store.UpsertAsync(job, ct);
        await bus.PublishAsync(new AgentEvent { Type = EventTypes.ScheduleTriggered, BotId = job.BotId, ThreadId = job.ThreadId, TaskId = task.Id, Message = job.Name }, ct);
        return task;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var job in await store.ListAsync(stoppingToken))
                {
                    if (!job.Enabled || job.NextRunAt is null || job.NextRunAt > now) continue;
                    try { await FireAsync(job, stoppingToken); }
                    catch (Exception ex) when (ex is BotValidationException or ArgumentException or InvalidOperationException)
                    {
                        log.LogWarning(ex, "Schedule {Job} failed to fire", job.Name);
                        job.NextRunAt = ComputeNext(job, now);
                        await store.UpsertAsync(job, stoppingToken);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Scheduler tick failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// .marbot packages: a ZIP with manifest.json, persona.md, skills/mcp lock files, optional exported skills,
/// schedules and sanitised memory, plus SHA-256 checksums. Secrets are never exported — only references.
/// </summary>
public sealed class BotPackageService(BotRegistry registry, SkillRegistry skills, IDocumentStore<McpServerConfig> mcp, IDocumentStore<ScheduleJob> schedules, IMemoryStore memory)
{
    public const int SchemaVersion = 1;

    public async Task<byte[]> ExportAsync(string botId, bool includeMemory, CancellationToken ct)
    {
        var bot = await registry.GetAsync(botId, ct) ?? throw new BotValidationException("Bot not found.");
        using var ms = new MemoryStream();
        var checksums = new Dictionary<string, string>();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string content)
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                checksums[path] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var e = zip.CreateEntry(path, CompressionLevel.Optimal);
                using var s = e.Open();
                s.Write(bytes);
            }

            var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(bot, MarbotsJsonContext.Default.BotDefinition), MarbotsJsonContext.Default.BotDefinition)!;
            copy.Status = BotStatus.Ready;
            Add("manifest.json", $$"""{"schemaVersion":{{SchemaVersion}},"exportedAt":"{{DateTimeOffset.UtcNow:O}}","generator":"Marbots 0.1 — {{WellKnown.CreditsEn}}","bot":{{JsonSerializer.Serialize(copy, MarbotsJsonContext.Default.BotDefinition)}}}""");
            Add("persona.md", bot.Persona);

            var skillLock = new List<SkillInfo>();
            foreach (var s in skills.ForBot(bot).Where(s => !bot.Skills.Contains("*")))
            {
                skillLock.Add(s);
                if (s.Source != "installed") continue;
                foreach (var f in Directory.EnumerateFiles(s.Path, "*", SearchOption.AllDirectories))
                    Add($"exported-skills/{Ids.Slug(s.Name)}/{Path.GetRelativePath(s.Path, f).Replace('\\', '/')}", await File.ReadAllTextAsync(f, ct));
            }
            Add("skills.lock.json", JsonSerializer.Serialize(skillLock.Select(s => new SkillInfo { Name = s.Name, Version = s.Version, Source = s.Source, Trust = s.Trust }).ToList(), MarbotsJsonContext.Indented.ListSkillInfo));

            var mcpLock = new List<McpServerConfig>();
            foreach (var id in bot.McpServers)
            {
                if (await mcp.GetAsync(id, ct) is not { } server) continue;
                mcpLock.Add(new McpServerConfig
                {
                    Id = server.Id, Name = server.Name, Description = server.Description, Transport = server.Transport, Command = server.Command,
                    Args = server.Args, Url = server.Url, Trust = server.Trust, PermissionProfile = server.PermissionProfile,
                    // Only secret references survive export; literal values are replaced.
                    Env = server.Env.ToDictionary(kv => kv.Key, kv => kv.Value.StartsWith("secret:", StringComparison.Ordinal) ? kv.Value : $"secret:{kv.Key}"),
                });
            }
            Add("mcp.lock.json", JsonSerializer.Serialize(mcpLock, MarbotsJsonContext.Indented.ListMcpServerConfig));

            var jobs = (await schedules.ListAsync(ct)).Where(j => j.BotId == bot.Id)
                .Select(j => new ScheduleJob { Id = j.Id, Name = j.Name, BotId = j.BotId, Prompt = j.Prompt, Cron = j.Cron, RunAt = j.RunAt, TimeZone = j.TimeZone, Enabled = false }).ToList();
            Add("schedules.json", JsonSerializer.Serialize(jobs, MarbotsJsonContext.Indented.ListScheduleJob));

            if (includeMemory)
            {
                var mems = (await memory.ListAsync(bot.Id, 1000, ct)).Where(m => m.Sensitivity != "secret" && !SecretScanner.LooksSensitive(m.Content)).ToList();
                Add("memory.json", JsonSerializer.Serialize(mems, MarbotsJsonContext.Indented.ListMemoryRecord));
            }
            var sums = JsonSerializer.Serialize(checksums, MarbotsJsonContext.Indented.DictionaryStringString);
            var ce = zip.CreateEntry("checksums.json");
            await using (var cs = ce.Open()) await cs.WriteAsync(Encoding.UTF8.GetBytes(sums), ct);
        }
        return ms.ToArray();
    }

    public async Task<BotDefinition> ImportAsync(Stream package, CancellationToken ct)
    {
        using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            if (e.FullName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(e.FullName)) throw new BotValidationException($"Unsafe path in package: {e.FullName}");
            total += e.Length;
            if (total > 50_000_000) throw new BotValidationException("Package is too large.");
            await using var s = e.Open();
            using var buf = new MemoryStream();
            await s.CopyToAsync(buf, ct);
            files[e.FullName] = buf.ToArray();
        }
        if (!files.TryGetValue("manifest.json", out var manifestBytes)) throw new BotValidationException("Not a .marbot package: manifest.json missing.");
        if (files.TryGetValue("checksums.json", out var sumBytes))
        {
            var sums = JsonSerializer.Deserialize(sumBytes, MarbotsJsonContext.Default.DictionaryStringString) ?? [];
            foreach (var (path, hash) in sums)
                if (!files.TryGetValue(path, out var data) || Convert.ToHexStringLower(SHA256.HashData(data)) != hash)
                    throw new BotValidationException($"Checksum mismatch for {path}. The package may be corrupted or tampered with.");
        }
        using var manifest = JsonDocument.Parse(manifestBytes);
        var version = manifest.RootElement.TryGetProperty("schemaVersion", out var v) ? v.GetInt32() : 1;
        if (version > SchemaVersion) throw new BotValidationException($"Package schema v{version} is newer than this Marbots version supports.");
        var bot = manifest.RootElement.GetProperty("bot").Deserialize(MarbotsJsonContext.Default.BotDefinition) ?? throw new BotValidationException("Invalid manifest.");

        // Install bundled skills that are not present yet.
        var skillRoot = skills.InstalledDirectory;
        foreach (var (path, data) in files.Where(f => f.Key.StartsWith("exported-skills/", StringComparison.Ordinal)))
        {
            var rel = path["exported-skills/".Length..];
            var dest = WorkspacePaths.Resolve(skillRoot, rel);
            if (File.Exists(dest)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await File.WriteAllBytesAsync(dest, data, ct);
        }
        skills.Refresh();

        // Imported MCP servers arrive disabled so the user reviews command and permissions first.
        if (files.TryGetValue("mcp.lock.json", out var mcpBytes))
        {
            foreach (var server in JsonSerializer.Deserialize(mcpBytes, MarbotsJsonContext.Default.ListMcpServerConfig) ?? [])
            {
                if (await mcp.GetAsync(server.Id, ct) is not null) continue;
                server.Enabled = false;
                server.Trust = "Unverified";
                server.IsCatalogEntry = false;
                await mcp.UpsertAsync(server, ct);
            }
        }

        bot.Id = "";
        bot.IsSystem = false;
        bot.KernelFunctions.Remove(KernelPacks.Management);
        if ((await registry.ListAsync(ct)).Any(b => b.Name.Equals(bot.Name, StringComparison.OrdinalIgnoreCase))) bot.Name += " (imported)";
        bot = await registry.CreateAsync(bot, ct);

        if (files.TryGetValue("memory.json", out var memBytes))
        {
            foreach (var m in JsonSerializer.Deserialize(memBytes, MarbotsJsonContext.Default.ListMemoryRecord) ?? [])
            {
                m.Id = "";
                m.Owner = bot.Id;
                m.Source = "import:" + m.Source;
                await memory.WriteAsync(m, ct);
            }
        }
        if (files.TryGetValue("schedules.json", out var jobBytes))
        {
            foreach (var j in JsonSerializer.Deserialize(jobBytes, MarbotsJsonContext.Default.ListScheduleJob) ?? [])
            {
                j.Id = Ids.New("job");
                j.BotId = bot.Id;
                j.Enabled = false;
                j.ThreadId = null;
                await schedules.UpsertAsync(j, ct);
            }
        }
        return bot;
    }
}

/// <summary>Host registry. The local host is always present; remote hosts arrive with the AgentHost service (roadmap phase 2).</summary>
public sealed class HostService(MarbotsOptions options, HostRegistry registry, HostConnectionManager connections)
{
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    /// <summary>The control plane's own host plus every enrolled remote host with its live status.</summary>
    public async Task<IReadOnlyList<HostInfo>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<HostInfo> { Local() };
        foreach (var h in await registry.ListAsync(ct))
        {
            var online = connections.IsOnline(h.Id);
            var hello = connections.HelloOf(h.Id) ?? h.LastHello;
            list.Add(new HostInfo
            {
                Id = h.Id, Name = h.Name, Kind = h.Kind, Os = hello?.Os ?? "", Architecture = hello?.Architecture ?? "",
                ProcessorCount = hello?.ProcessorCount ?? 0, TotalMemoryMb = hello?.TotalMemoryMb ?? 0, AgentVersion = hello?.AgentVersion ?? "",
                Status = h.Disabled ? "Disabled" : online ? "Online" : "Offline", Capabilities = hello?.Capabilities ?? [],
                Metrics = connections.MetricsOf(h.Id) ?? h.LastMetrics, InstalledVia = h.InstalledVia,
                LastHeartbeat = online ? DateTimeOffset.UtcNow : h.LastSeen ?? h.EnrolledAt, StartedAt = h.EnrolledAt,
            });
        }
        return list;
    }

    public HostInfo Local()
    {
        var gc = GC.GetGCMemoryInfo();
        using var proc = Process.GetCurrentProcess();
        return new HostInfo
        {
            Id = WellKnown.LocalHostId,
            Name = Environment.MachineName,
            Kind = "local",
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            TotalMemoryMb = gc.TotalAvailableMemoryBytes / 1024 / 1024,
            ProcessWorkingSetMb = proc.WorkingSet64 / 1024 / 1024,
            AgentVersion = typeof(HostService).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            Status = "Online",
            Capabilities = OperatingSystem.IsWindows() ? ["shell", "desktop"] : ["shell"],
            LastHeartbeat = DateTimeOffset.UtcNow,
            StartedAt = _started,
        };
    }

    public string DataDirectory => Path.GetFullPath(options.DataDirectory);
}
