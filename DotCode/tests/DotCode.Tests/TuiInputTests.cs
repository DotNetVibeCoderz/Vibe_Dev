using DotCode.Tui.Components;
using DotCode.Tui.Input;
using DotCode.Tui.Rendering;
using DotCode.Tui.Themes;

namespace DotCode.Tests;

public sealed class TuiInputTests
{
    private static ConsoleKeyInfo K(char c) => new(c, c switch
    {
        >= 'a' and <= 'z' => ConsoleKey.A + (c - 'a'),
        >= 'A' and <= 'Z' => ConsoleKey.A + (c - 'A'),
        >= '0' and <= '9' => ConsoleKey.D0 + (c - '0'),
        _ => ConsoleKey.Oem1,
    }, char.IsUpper(c), false, false);

    private static readonly ConsoleKeyInfo Esc = new('\u001b', ConsoleKey.Escape, false, false, false);

    /// <summary>Types keys: in insert mode as text, in normal mode through vim. "\u001b" = Esc.</summary>
    private static void Keys(VimMode vim, InputEditor e, string keys)
    {
        foreach (var c in keys)
        {
            if (c == '\u001b') { if (vim.State == VimState.Insert) vim.EnterNormal(e); else vim.HandleNormal(Esc, e); continue; }
            if (vim.State == VimState.Insert) e.Insert(c.ToString());
            else vim.HandleNormal(K(c), e);
        }
    }

    [Fact]
    public void History_search_finds_newest_match_and_walks_older()
    {
        List<string> history = ["git status", "dotnet build", "git push", "dotnet test", "git push"];
        var s = new HistorySearch(history, "draft");
        s.Type("g");
        s.Type("it");
        Assert.Equal("git push", s.Match);
        Assert.Equal(4, s.MatchIndex);
        s.Older();                         // skips the duplicate "git push"
        Assert.Equal("git status", s.Match);
        s.Older();
        Assert.True(s.Failed);
        Assert.Equal("git status", s.Match);
        s.Backspace(); s.Backspace(); s.Backspace();
        Assert.Null(s.Match);
        s.Type("TEST");                    // case-insensitive
        Assert.Equal("dotnet test", s.Match);
        s.Type("zzz");
        Assert.True(s.Failed);
        Assert.Equal("draft", s.Original);
    }

    [Fact]
    public void Vim_motions_and_operators_edit_the_prompt()
    {
        var e = new InputEditor([]);
        var vim = new VimMode();
        Keys(vim, e, "hello brave new world\u001b");
        Assert.Equal(VimState.Normal, vim.State);
        Assert.Equal(20, e.Cursor);                     // on the last character
        Keys(vim, e, "0dw");
        Assert.Equal("brave new world", e.Text);
        Keys(vim, e, "wcwold\u001b");
        Assert.Equal("brave old world", e.Text);
        Keys(vim, e, "$x");
        Assert.Equal("brave old worl", e.Text);
        Keys(vim, e, "0yeP");
        Assert.Equal("bravebrave old worl", e.Text);
        Keys(vim, e, "u");
        Assert.Equal("brave old worl", e.Text);
        Keys(vim, e, "2wD");
        Assert.Equal("brave old ", e.Text);
        Keys(vim, e, "0~");
        Assert.Equal("Brave old ", e.Text);
        Keys(vim, e, "A!\u001b");
        Assert.Equal("Brave old !", e.Text);
        Keys(vim, e, "0rb");
        Assert.Equal("brave old !", e.Text);
        Keys(vim, e, "cc");
        Assert.Equal(VimState.Insert, vim.State);
        Assert.Equal("", e.Text);
    }

    [Fact]
    public void Vim_linewise_operations_and_counts()
    {
        var e = new InputEditor([]);
        var vim = new VimMode();
        Keys(vim, e, "one\ntwo\nthree\u001b");
        Keys(vim, e, "ggdd");
        Assert.Equal("two\nthree", e.Text);
        Keys(vim, e, "p");
        Assert.Equal("two\none\nthree", e.Text);
        Keys(vim, e, "gg2dd");
        Assert.Equal("three", e.Text);
        Keys(vim, e, "othen\u001b");
        Assert.Equal("three\nthen", e.Text);
        Keys(vim, e, "gg3x");
        Assert.Equal("ee\nthen", e.Text);
        Keys(vim, e, "yyjp");
        Assert.Equal("ee\nthen\nee", e.Text);
        // j on the last line asks the host for history.
        Assert.Equal(VimMode.Result.HistoryNext, vim.HandleNormal(K('j'), e));
    }

    [Fact]
    public void Ui_text_resolves_language()
    {
        Assert.Same(UiText.English, UiText.For(null));
        Assert.Same(UiText.Indonesian, UiText.For("id"));
        Assert.Same(UiText.English, UiText.For("fr"));
        // Both languages define every string.
        foreach (var prop in typeof(UiText).GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name != nameof(UiText.Code)))
        {
            Assert.False(string.IsNullOrEmpty((string?)prop.GetValue(UiText.English)), prop.Name);
            Assert.False(string.IsNullOrEmpty((string?)prop.GetValue(UiText.Indonesian)), prop.Name);
        }
        Assert.Equal(UiText.English.Tips.Length, UiText.Indonesian.Tips.Length);
        Assert.Equal(UiText.English.Shortcuts.Length, UiText.Indonesian.Shortcuts.Length);
    }

    [Fact]
    public void Transcript_viewer_scrolls_and_searches()
    {
        var lines = Enumerable.Range(1, 100).Select(i => i == 42 ? "needle here" : $"line {i}").ToList();
        var viewer = new TranscriptModal(lines) { Height = 12 };
        var blocks = new Blocks(Theme.Dark());
        var frame = viewer.Render(Theme.Dark(), blocks, 60).Select(Ansi.Strip).ToList();
        Assert.Equal(12, frame.Count);
        Assert.Equal("line 100", frame[^2]);            // starts at the bottom
        viewer.HandleKey(new ConsoleKeyInfo('g', ConsoleKey.G, false, false, false));
        Assert.Equal(0, viewer.Top);
        viewer.HandleKey(new ConsoleKeyInfo('/', ConsoleKey.Oem2, false, false, false));
        foreach (var c in "NEEDLE") viewer.HandleKey(new ConsoleKeyInfo(c, ConsoleKey.A, true, false, false));
        viewer.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        Assert.Equal(41, viewer.Top);
        Assert.True(viewer.HandleKey(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false)));
    }
}
