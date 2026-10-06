using Markdig;
using Marbots.Abstractions;

namespace Marbots.Server.Services;

/// <summary>Renders bot output as safe HTML (raw HTML in model output is escaped).</summary>
public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public string ToHtml(string? markdown) => string.IsNullOrEmpty(markdown) ? "" : Markdown.ToHtml(markdown, _pipeline);
}

public sealed record UsageByBot(string BotId, int Tasks, long InputTokens, long OutputTokens, decimal CostUsd);

public sealed record UsageSummary(int Tasks, int Completed, int Failed, int Running, long InputTokens, long OutputTokens, decimal CostUsd, IReadOnlyList<UsageByBot> ByBot, IReadOnlyList<(DateOnly Day, int Tasks, decimal Cost)> Daily);

public sealed class UsageService(IDocumentStore<TaskRecord> tasks)
{
    public async Task<UsageSummary> SummaryAsync()
    {
        var all = await tasks.ListAsync();
        var byBot = all.GroupBy(t => t.BotId)
            .Select(g => new UsageByBot(g.Key, g.Count(), g.Sum(t => t.InputTokens), g.Sum(t => t.OutputTokens), g.Sum(t => t.CostUsd)))
            .OrderByDescending(b => b.CostUsd).ToList();
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-13));
        var daily = Enumerable.Range(0, 14).Select(i => since.AddDays(i))
            .Select(d => (d, all.Count(t => DateOnly.FromDateTime(t.CreatedAt.UtcDateTime) == d), all.Where(t => DateOnly.FromDateTime(t.CreatedAt.UtcDateTime) == d).Sum(t => t.CostUsd)))
            .ToList();
        return new UsageSummary(all.Count, all.Count(t => t.State == TaskState.Completed), all.Count(t => t.State == TaskState.Failed),
            all.Count(t => !t.State.IsTerminal()), all.Sum(t => t.InputTokens), all.Sum(t => t.OutputTokens), all.Sum(t => t.CostUsd), byBot, daily);
    }
}

/// <summary>Tiny bilingual UI dictionary (English / Bahasa Indonesia). Language is chosen per browser.</summary>
public sealed class UiText
{
    public string Lang { get; set; } = "en";
    public event Action? Changed;

    public void Set(string lang)
    {
        Lang = lang == "id" ? "id" : "en";
        Changed?.Invoke();
    }

    public string this[string key] => (Lang == "id" ? Id : En).TryGetValue(key, out var v) ? v : En.GetValueOrDefault(key, key);

    private static readonly Dictionary<string, string> En = new()
    {
        ["nav.chat"] = "Chat", ["nav.team"] = "Team", ["nav.templates"] = "Templates", ["nav.tasks"] = "Tasks", ["nav.office"] = "Office",
        ["nav.approvals"] = "Approvals", ["nav.skills"] = "Skills", ["nav.mcp"] = "MCP servers", ["nav.schedules"] = "Schedules",
        ["nav.memory"] = "Memory", ["nav.dashboard"] = "Dashboard", ["nav.settings"] = "Settings", ["nav.about"] = "About",
        ["nav.group.work"] = "Work", ["nav.group.build"] = "Build", ["nav.group.observe"] = "Observe",
        ["chat.placeholder"] = "Message {0}… (Enter to send, Shift+Enter for a new line)", ["chat.send"] = "Send", ["chat.stop"] = "Stop",
        ["chat.newThread"] = "New thread", ["chat.threads"] = "Threads", ["chat.activity"] = "Live activity", ["chat.files"] = "Workspace files",
        ["chat.empty.title"] = "Give Boss Man a goal", ["chat.empty.body"] = "Describe the outcome you want. Boss Man plans it, hands parts to the team, and reports back with results.",
        ["chat.reset"] = "Reset context", ["chat.fork"] = "Fork", ["chat.export"] = "Export", ["chat.pin"] = "Pin", ["chat.unpin"] = "Unpin", ["chat.archive"] = "Archive",
        ["chat.working"] = "is working", ["chat.noActivity"] = "Activity from the bots appears here while they work.",
        ["team.title"] = "Your team", ["team.subtitle"] = "Bots are durable teammates with their own persona, memory, skills and tools.",
        ["team.new"] = "Create bot", ["team.import"] = "Import .marbot", ["team.fromTemplate"] = "Hire from template",
        ["tpl.title"] = "Template gallery", ["tpl.subtitle"] = "Start from a role. Every template is editable after hiring.", ["tpl.hire"] = "Hire", ["tpl.new"] = "Create template",
        ["tpl.search"] = "Search roles, skills, tags…", ["tpl.all"] = "All",
        ["approvals.title"] = "Approvals", ["approvals.subtitle"] = "Risky actions wait here for your decision.", ["approvals.none"] = "Nothing is waiting for you.",
        ["approve.once"] = "Approve once", ["approve.session"] = "Approve for this thread", ["reject"] = "Reject",
        ["save"] = "Save", ["cancel"] = "Cancel", ["delete"] = "Delete", ["edit"] = "Edit", ["chat"] = "Chat", ["export"] = "Export", ["pause"] = "Pause", ["resume"] = "Resume",
        ["credits"] = WellKnown.CreditsEn,
        ["model.label"] = "Model", ["model.default"] = "Default model ({0})", ["model.profiles"] = "Profiles", ["model.models"] = "Models",
        ["model.custom"] = "Other provider/model…", ["model.uses"] = "Uses {0}",
        ["model.followsDefault"] = " — follows the workspace default, so it changes when you change the default in Settings.",
        ["skip.title"] = "Skip approvals (dangerous)",
        ["skip.body"] = "Like --dangerously-skip-permissions: bots run shell commands, delete files and send external messages without asking you. Actions a bot's permission profile forbids stay forbidden. Use only in a sandbox or when you fully trust every bot and every input.",
        ["skip.on"] = "Approvals are skipped", ["skip.off"] = "Approvals are required",
        ["skip.enable"] = "Skip approvals", ["skip.disable"] = "Require approvals again",
        ["skip.banner"] = "Dangerous mode: bots act without asking for approval.",
    };

    private static readonly Dictionary<string, string> Id = new()
    {
        ["nav.chat"] = "Obrolan", ["nav.team"] = "Tim", ["nav.templates"] = "Templat", ["nav.tasks"] = "Tugas", ["nav.office"] = "Kantor",
        ["nav.approvals"] = "Persetujuan", ["nav.skills"] = "Skill", ["nav.mcp"] = "Server MCP", ["nav.schedules"] = "Jadwal",
        ["nav.memory"] = "Memori", ["nav.dashboard"] = "Dasbor", ["nav.settings"] = "Pengaturan", ["nav.about"] = "Tentang",
        ["nav.group.work"] = "Kerja", ["nav.group.build"] = "Bangun", ["nav.group.observe"] = "Pantau",
        ["chat.placeholder"] = "Kirim pesan ke {0}… (Enter untuk kirim, Shift+Enter untuk baris baru)", ["chat.send"] = "Kirim", ["chat.stop"] = "Hentikan",
        ["chat.newThread"] = "Utas baru", ["chat.threads"] = "Utas", ["chat.activity"] = "Aktivitas langsung", ["chat.files"] = "Berkas workspace",
        ["chat.empty.title"] = "Beri Boss Man sebuah tujuan", ["chat.empty.body"] = "Jelaskan hasil yang Anda inginkan. Boss Man merencanakannya, membagi pekerjaan ke tim, lalu melaporkan hasilnya.",
        ["chat.reset"] = "Reset konteks", ["chat.fork"] = "Cabangkan", ["chat.export"] = "Ekspor", ["chat.pin"] = "Sematkan", ["chat.unpin"] = "Lepas sematan", ["chat.archive"] = "Arsipkan",
        ["chat.working"] = "sedang bekerja", ["chat.noActivity"] = "Aktivitas bot akan muncul di sini saat mereka bekerja.",
        ["team.title"] = "Tim Anda", ["team.subtitle"] = "Bot adalah rekan kerja tetap dengan persona, memori, skill, dan tools masing-masing.",
        ["team.new"] = "Buat bot", ["team.import"] = "Impor .marbot", ["team.fromTemplate"] = "Rekrut dari templat",
        ["tpl.title"] = "Galeri templat", ["tpl.subtitle"] = "Mulai dari sebuah peran. Semua templat bisa diubah setelah direkrut.", ["tpl.hire"] = "Rekrut", ["tpl.new"] = "Buat templat",
        ["tpl.search"] = "Cari peran, skill, tag…", ["tpl.all"] = "Semua",
        ["approvals.title"] = "Persetujuan", ["approvals.subtitle"] = "Tindakan berisiko menunggu keputusan Anda di sini.", ["approvals.none"] = "Tidak ada yang menunggu Anda.",
        ["approve.once"] = "Setujui sekali", ["approve.session"] = "Setujui untuk utas ini", ["reject"] = "Tolak",
        ["save"] = "Simpan", ["cancel"] = "Batal", ["delete"] = "Hapus", ["edit"] = "Ubah", ["chat"] = "Obrolan", ["export"] = "Ekspor", ["pause"] = "Jeda", ["resume"] = "Lanjutkan",
        ["credits"] = WellKnown.Credits,
        ["model.label"] = "Model", ["model.default"] = "Model bawaan ({0})", ["model.profiles"] = "Profil", ["model.models"] = "Model",
        ["model.custom"] = "Provider/model lain…", ["model.uses"] = "Memakai {0}",
        ["model.followsDefault"] = " — mengikuti model bawaan workspace, jadi ikut berubah saat model bawaan diganti di Pengaturan.",
        ["skip.title"] = "Lewati persetujuan (berbahaya)",
        ["skip.body"] = "Seperti --dangerously-skip-permissions: bot menjalankan perintah shell, menghapus berkas, dan mengirim pesan eksternal tanpa bertanya. Aksi yang dilarang profil izin bot tetap dilarang. Gunakan hanya di sandbox atau jika Anda sepenuhnya memercayai semua bot dan semua masukan.",
        ["skip.on"] = "Persetujuan dilewati", ["skip.off"] = "Persetujuan diperlukan",
        ["skip.enable"] = "Lewati persetujuan", ["skip.disable"] = "Wajibkan persetujuan lagi",
        ["skip.banner"] = "Mode berbahaya: bot bertindak tanpa meminta persetujuan.",
    };
}
