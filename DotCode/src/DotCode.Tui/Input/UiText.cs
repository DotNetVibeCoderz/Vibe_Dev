using System.Globalization;

namespace DotCode.Tui.Input;

/// <summary>User-facing TUI strings in English and Bahasa Indonesia. Selected with <c>tui.language</c>
/// ("en" | "id" | "auto" — auto follows DOTCODE_LANG / the OS UI culture). Model output is not affected.</summary>
public sealed class UiText
{
    private static UiText? _current;

    // Not a field initializer: static initializers run in declaration order and English is declared below.
    public static UiText Current
    {
        get => _current ?? English;
        set => _current = value;
    }

    public string Code { get; private init; } = "en";

    // footer / modes
    public string ShortcutsHint { get; private init; } = "";
    public string CycleHint { get; private init; } = "";
    public string AcceptEditsOn { get; private init; } = "";
    public string PlanModeOn { get; private init; } = "";
    public string BypassOn { get; private init; } = "";
    public string AutoModeOn { get; private init; } = "";
    public string ContextLeft { get; private init; } = "";   // {0} = percent
    public string Queued { get; private init; } = "";
    public string QueuePlaceholder { get; private init; } = "";
    public string[] Ideas { get; private init; } = [];
    public string[][] Shortcuts { get; private init; } = [];
    public string TipPrefix { get; private init; } = "";
    public string[] Tips { get; private init; } = [];

    // flashes and inline messages
    public string PressCtrlCAgain { get; private init; } = "";
    public string EscAgainToClear { get; private init; } = "";
    public string NoTodos { get; private init; } = "";
    public string Interrupted { get; private init; } = "";
    public string WhatInstead { get; private init; } = "";
    public string EscToInterrupt { get; private init; } = "";
    public string Thinking { get; private init; } = "";
    public string VimOn { get; private init; } = "";
    public string VimOff { get; private init; } = "";

    // history search / transcript
    public string SearchHistory { get; private init; } = "";
    public string NoMatch { get; private init; } = "";
    public string SearchHint { get; private init; } = "";
    public string Transcript { get; private init; } = "";
    public string TranscriptHint { get; private init; } = "";
    public string TranscriptSearch { get; private init; } = "";
    public string TranscriptEmpty { get; private init; } = "";

    // permission dialog
    public string Proceed { get; private init; } = "";
    public string MakeEdit { get; private init; } = "";         // {0} = file
    public string Create { get; private init; } = "";           // {0} = file
    public string Yes { get; private init; } = "";
    public string YesAllEdits { get; private init; } = "";
    public string YesCommands { get; private init; } = "";      // {0} = command prefix, {1} = project
    public string YesDomain { get; private init; } = "";        // {0} = domain
    public string YesRule { get; private init; } = "";          // {0} = rule, {1} = project
    public string No { get; private init; } = "";

    // banner
    public string WelcomeBack { get; private init; } = "";      // {0} = user
    public string TipsTitle { get; private init; } = "";
    public string TipHelp { get; private init; } = "";          // {0} = /help
    public string TipInit { get; private init; } = "";          // {0} = /init
    public string TipModel { get; private init; } = "";         // {0} = /model
    public string TipModes { get; private init; } = "";         // {0} = shift+tab, {1} = /theme
    public string RecentActivity { get; private init; } = "";
    public string NoRecentActivity { get; private init; } = "";
    public string ResumeForMore { get; private init; } = "";

    // exit summary
    public string TotalCost { get; private init; } = "";
    public string DurationApi { get; private init; } = "";
    public string DurationWall { get; private init; } = "";
    public string CodeChanges { get; private init; } = "";
    public string LinesChanged { get; private init; } = "";     // {0} added, {1} removed
    public string UsageByModel { get; private init; } = "";
    public string ResumeWith { get; private init; } = "";

    // notifications
    public string NotifyDone { get; private init; } = "";
    public string NotifyWaiting { get; private init; } = "";

    public static readonly UiText English = new()
    {
        Code = "en",
        ShortcutsHint = "? for shortcuts",
        CycleHint = " (shift+tab to cycle)",
        AcceptEditsOn = "accept edits on",
        PlanModeOn = "plan mode on",
        BypassOn = "bypass permissions on",
        AutoModeOn = "auto mode on",
        ContextLeft = "Context left until auto-compact: {0}%",
        Queued = "(queued)",
        QueuePlaceholder = "Type to queue a message for when DotCode finishes…",
        Ideas = ["Try \"explain this codebase\"", "Try \"write a test for <filepath>\"", "Try \"fix the failing build\"", "Try \"refactor <filepath> to be more readable\"", "Try \"how does <feature> work?\""],
        Shortcuts =
        [
            ["! for bash mode", "/ for commands", "@ for file paths", "# to memorize", "ctrl + r to search history"],
            ["double tap esc to clear input", "shift + tab to cycle modes", "ctrl + o for transcript", "ctrl + t to show todos", "/vim for vim mode"],
            ["\\⏎ or shift + ⏎ for newline", "ctrl + _ / ctrl + z to undo", "ctrl + l to clear screen", "ctrl + c twice to exit", ""],
        ],
        TipPrefix = "Tip: ",
        Tips =
        [
            "Press shift+tab to switch between default, accept-edits and plan mode",
            "Use /model to switch to any configured LLM (Anthropic, OpenAI, Gemini, DeepSeek, Ollama…)",
            "Start a line with ! to run a shell command directly",
            "Type @ to mention files — their contents are attached automatically",
            "Use /compact to summarize long conversations and free context",
            "Press esc twice to rewind the conversation (and your code)",
            "Hit ctrl+o to open the full transcript, ctrl+r to search your prompt history",
            "Create reusable prompts in .dotcode/commands/*.md",
            "Add MCP servers with: dotcode mcp add <name> <command>",
            "Queue follow-up messages while DotCode is working — just type and press enter",
            "Try /theme to pick colors, glyphs and spinner styles",
            "Use /agents to see available subagents; the model delegates big searches to them",
            "Prefer vim keys? Run /vim",
        ],
        PressCtrlCAgain = "Press Ctrl-C again to exit",
        EscAgainToClear = "Esc again to clear",
        NoTodos = "No todos yet",
        Interrupted = "Interrupted",
        WhatInstead = " · What should DotCode do instead?",
        EscToInterrupt = " to interrupt",
        Thinking = "thinking",
        VimOn = "Vim mode on (esc for NORMAL, i for INSERT)",
        VimOff = "Vim mode off",
        SearchHistory = "search history: ",
        NoMatch = "no match",
        SearchHint = "enter select · ctrl+r older · esc cancel",
        Transcript = "Transcript",
        TranscriptHint = "↑↓/jk scroll · pgup/pgdn · g/G top/bottom · / search · n/N next/prev · esc close",
        TranscriptSearch = "search: ",
        TranscriptEmpty = "(nothing yet)",
        Proceed = "Do you want to proceed?",
        MakeEdit = "Do you want to make this edit to {0}?",
        Create = "Do you want to create {0}?",
        Yes = "Yes",
        YesAllEdits = "Yes, allow all edits during this session (shift+tab)",
        YesCommands = "Yes, and don't ask again for {0} commands in {1}",
        YesDomain = "Yes, and don't ask again for {0}",
        YesRule = "Yes, and don't ask again for {0} in {1}",
        No = "No, and tell DotCode what to do differently (esc)",
        WelcomeBack = "Welcome back {0}!",
        TipsTitle = "Tips for getting started",
        TipHelp = "Run {0} to see commands and shortcuts",
        TipInit = "Run {0} to create a DOTCODE.md file with instructions",
        TipModel = "Use {0} to switch between any configured LLM",
        TipModes = "{0} cycles permission modes · {1} restyles",
        RecentActivity = "Recent activity",
        NoRecentActivity = "No recent activity",
        ResumeForMore = "/resume for more",
        TotalCost = "Total cost:            ",
        DurationApi = "Total duration (API):  ",
        DurationWall = "Total duration (wall): ",
        CodeChanges = "Total code changes:    ",
        LinesChanged = "{0} lines added, {1} lines removed",
        UsageByModel = "Usage by model:",
        ResumeWith = "Resume this session with:",
        NotifyDone = "DotCode finished and is waiting for your input",
        NotifyWaiting = "DotCode needs your attention",
    };

    public static readonly UiText Indonesian = new()
    {
        Code = "id",
        ShortcutsHint = "? untuk pintasan",
        CycleHint = " (shift+tab untuk ganti)",
        AcceptEditsOn = "terima edit aktif",
        PlanModeOn = "mode rencana aktif",
        BypassOn = "lewati izin aktif",
        AutoModeOn = "mode otomatis aktif",
        ContextLeft = "Sisa konteks sebelum auto-compact: {0}%",
        Queued = "(antre)",
        QueuePlaceholder = "Ketik untuk mengantrekan pesan sampai DotCode selesai…",
        Ideas = ["Coba \"jelaskan codebase ini\"", "Coba \"tulis test untuk <filepath>\"", "Coba \"perbaiki build yang gagal\"", "Coba \"refactor <filepath> agar lebih mudah dibaca\"", "Coba \"bagaimana <fitur> bekerja?\""],
        Shortcuts =
        [
            ["! untuk mode bash", "/ untuk perintah", "@ untuk path file", "# untuk mengingat", "ctrl + r cari riwayat"],
            ["esc dua kali hapus input", "shift + tab ganti mode", "ctrl + o untuk transkrip", "ctrl + t tampilkan todo", "/vim untuk mode vim"],
            ["\\⏎ atau shift + ⏎ baris baru", "ctrl + _ / ctrl + z batalkan", "ctrl + l bersihkan layar", "ctrl + c dua kali keluar", ""],
        ],
        TipPrefix = "Tips: ",
        Tips =
        [
            "Tekan shift+tab untuk berpindah antara mode default, terima-edit dan rencana",
            "Gunakan /model untuk beralih ke LLM mana pun (Anthropic, OpenAI, Gemini, DeepSeek, Ollama…)",
            "Awali baris dengan ! untuk menjalankan perintah shell langsung",
            "Ketik @ untuk menyebut file — isinya dilampirkan otomatis",
            "Gunakan /compact untuk meringkas percakapan panjang dan membebaskan konteks",
            "Tekan esc dua kali untuk memutar balik percakapan (dan kode Anda)",
            "Tekan ctrl+o untuk membuka transkrip lengkap, ctrl+r untuk mencari riwayat prompt",
            "Buat prompt yang bisa dipakai ulang di .dotcode/commands/*.md",
            "Tambahkan server MCP dengan: dotcode mcp add <nama> <perintah>",
            "Antrekan pesan lanjutan saat DotCode bekerja — cukup ketik lalu tekan enter",
            "Coba /theme untuk memilih warna, glyph dan gaya spinner",
            "Gunakan /agents untuk melihat subagent; model mendelegasikan pencarian besar ke mereka",
            "Suka tombol vim? Jalankan /vim",
        ],
        PressCtrlCAgain = "Tekan Ctrl-C sekali lagi untuk keluar",
        EscAgainToClear = "Esc sekali lagi untuk menghapus",
        NoTodos = "Belum ada todo",
        Interrupted = "Dihentikan",
        WhatInstead = " · Apa yang sebaiknya DotCode lakukan?",
        EscToInterrupt = " untuk menghentikan",
        Thinking = "berpikir",
        VimOn = "Mode vim aktif (esc untuk NORMAL, i untuk INSERT)",
        VimOff = "Mode vim nonaktif",
        SearchHistory = "cari riwayat: ",
        NoMatch = "tidak ditemukan",
        SearchHint = "enter pilih · ctrl+r lebih lama · esc batal",
        Transcript = "Transkrip",
        TranscriptHint = "↑↓/jk gulir · pgup/pgdn · g/G awal/akhir · / cari · n/N berikut/sebelum · esc tutup",
        TranscriptSearch = "cari: ",
        TranscriptEmpty = "(belum ada apa-apa)",
        Proceed = "Lanjutkan?",
        MakeEdit = "Terapkan edit ini ke {0}?",
        Create = "Buat file {0}?",
        Yes = "Ya",
        YesAllEdits = "Ya, izinkan semua edit selama sesi ini (shift+tab)",
        YesCommands = "Ya, dan jangan tanya lagi untuk perintah {0} di {1}",
        YesDomain = "Ya, dan jangan tanya lagi untuk {0}",
        YesRule = "Ya, dan jangan tanya lagi untuk {0} di {1}",
        No = "Tidak, dan beri tahu DotCode apa yang harus dilakukan (esc)",
        WelcomeBack = "Selamat datang kembali {0}!",
        TipsTitle = "Tips untuk memulai",
        TipHelp = "Jalankan {0} untuk melihat perintah dan pintasan",
        TipInit = "Jalankan {0} untuk membuat file DOTCODE.md berisi instruksi",
        TipModel = "Gunakan {0} untuk beralih antar LLM yang dikonfigurasi",
        TipModes = "{0} mengganti mode izin · {1} mengubah tampilan",
        RecentActivity = "Aktivitas terbaru",
        NoRecentActivity = "Belum ada aktivitas",
        ResumeForMore = "/resume untuk lainnya",
        TotalCost = "Total biaya:           ",
        DurationApi = "Total durasi (API):    ",
        DurationWall = "Total durasi (nyata):  ",
        CodeChanges = "Total perubahan kode:  ",
        LinesChanged = "{0} baris ditambah, {1} baris dihapus",
        UsageByModel = "Pemakaian per model:",
        ResumeWith = "Lanjutkan sesi ini dengan:",
        NotifyDone = "DotCode selesai dan menunggu input Anda",
        NotifyWaiting = "DotCode membutuhkan perhatian Anda",
    };

    /// <summary>Resolves "en" / "id" / "auto" (DOTCODE_LANG, then the OS UI culture); unknown values fall back to English.</summary>
    public static UiText For(string? language)
    {
        var lang = (language ?? "en").Trim().ToLowerInvariant();
        if (lang == "auto")
            lang = Environment.GetEnvironmentVariable("DOTCODE_LANG") is { Length: > 0 } env
                ? env.ToLowerInvariant()
                : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return lang.StartsWith("id", StringComparison.Ordinal) || lang.StartsWith("in", StringComparison.Ordinal) ? Indonesian : English;
    }
}
