using DotCode.Engine;
using DotCode.Engine.Agent;
using DotCode.Engine.Permissions;
using DotCode.Abstractions;
using DotCode.Tui.Components;
using DotCode.Tui.Input;
using DotCode.Tui.Rendering;

namespace DotCode.Tui;

internal sealed partial class App
{
    private void HandleKey(ConsoleKeyInfo key, bool burst)
    {
        var ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
        var shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
        var alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;

        // Ctrl+C: clear input → interrupt → (twice) exit
        if (ctrl && key.Key == ConsoleKey.C || key.KeyChar == '\u0003')
        {
            if (_modal is not null) { _modal.Cancel(); NextModal(); return; }
            if (_search is not null) { _input.SetText(_search.Original); _search = null; return; }
            if (!_input.IsEmpty) { _input.Clear(); return; }
            if (_busy) { Interrupt(); return; }
            if ((DateTime.UtcNow - _lastCtrlC).TotalSeconds < 2) { _exit = true; return; }
            _lastCtrlC = DateTime.UtcNow;
            Flash(UiText.Current.PressCtrlCAgain);
            return;
        }

        if (_modal is not null)
        {
            if (_modal.HandleKey(key)) NextModal();
            return;
        }

        if (_search is not null) { HandleSearchKey(key, ctrl); return; }
        if (ctrl && key.Key == ConsoleKey.R)
        {
            _search = new HistorySearch(_history, _input.Text);
            _suggestions.Clear();
            _showShortcuts = false;
            return;
        }
        if (ctrl && key.Key == ConsoleKey.O) { OpenTranscript(); return; }

        if (ctrl && key.Key == ConsoleKey.D && _input.IsEmpty) { _exit = true; return; }

        // Shift+Tab: cycle permission modes
        if (key.Key == ConsoleKey.Tab && shift)
        {
            _session.SetMode(_session.Mode.Next(_session.BypassAvailable, _session.AutoModeAvailable));
            return;
        }

        if (_vim is not null)
        {
            // Esc leaves insert mode — unless it is needed to close suggestions or interrupt a running turn.
            if (_vim.State == VimState.Insert && key.Key == ConsoleKey.Escape && _suggestions.Count == 0 && !(_busy && _input.IsEmpty))
            {
                _vim.EnterNormal(_input);
                return;
            }
            if (_vim.State == VimState.Normal)
            {
                switch (_vim.HandleNormal(key, _input))
                {
                    case VimMode.Result.Handled: return;
                    case VimMode.Result.HistoryPrev: _input.HistoryPrev(); _input.SetCursor(0); return;
                    case VimMode.Result.HistoryNext: _input.HistoryNext(); _input.SetCursor(0); return;
                }
            }
        }

        if (key.Key == ConsoleKey.Escape)
        {
            if (_suggestions.Count > 0) { _suggestions.Clear(); _suppressSuggestions = _input.Text; return; }
            if (_showShortcuts) { _showShortcuts = false; return; }
            if (_busy) { Interrupt(); return; }
            var doubleTap = (DateTime.UtcNow - _lastEsc).TotalMilliseconds < 800;
            _lastEsc = DateTime.UtcNow;
            if (!_input.IsEmpty)
            {
                if (doubleTap) _input.Clear(); else Flash(UiText.Current.EscAgainToClear);
                return;
            }
            if (doubleTap) OpenRewind();
            return;
        }

        if (ctrl)
        {
            switch (key.Key)
            {
                case ConsoleKey.T: _showTodos = !_showTodos; if (!_busy) ShowTodos(); return;
                case ConsoleKey.L: _screen.ClearScreen(); _anyCommitted = false; return;
                case ConsoleKey.A: _input.Home(); return;
                case ConsoleKey.E: _input.End(); return;
                case ConsoleKey.B: _input.Left(); return;
                case ConsoleKey.F: _input.Right(); return;
                case ConsoleKey.U: _input.KillToLineStart(); return;
                case ConsoleKey.K: _input.KillToLineEnd(); return;
                case ConsoleKey.W or ConsoleKey.Backspace: _input.DeleteWordBack(); return;
                case ConsoleKey.Y: _input.Yank(); return;
                case ConsoleKey.Z or ConsoleKey.OemMinus: _input.Undo(); return;
                case ConsoleKey.P: _input.HistoryPrev(); return;
                case ConsoleKey.N: _input.HistoryNext(); return;
                case ConsoleKey.J: _input.Insert("\n"); return;
                case ConsoleKey.H: _input.Backspace(); return;
                case ConsoleKey.LeftArrow: _input.WordLeft(); return;
                case ConsoleKey.RightArrow: _input.WordRight(); return;
                case ConsoleKey.Delete: _input.DeleteWordForward(); return;
            }
            if (key.KeyChar == '\u001f') { _input.Undo(); return; } // ctrl+_
        }
        if (alt)
        {
            switch (key.Key)
            {
                case ConsoleKey.B or ConsoleKey.LeftArrow: _input.WordLeft(); return;
                case ConsoleKey.F or ConsoleKey.RightArrow: _input.WordRight(); return;
                case ConsoleKey.D: _input.DeleteWordForward(); return;
                case ConsoleKey.Backspace: _input.DeleteWordBack(); return;
                case ConsoleKey.Enter: _input.Insert("\n"); return;
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                if (burst || shift) { _input.Insert("\n"); return; }
                if (_input.Cursor > 0 && _input.Text[_input.Cursor - 1] == '\\')
                {
                    _input.Backspace();
                    _input.Insert("\n");
                    return;
                }
                if (_suggestions.Count > 0 && AcceptSuggestion(submitIfCommand: true)) return;
                var text = _input.ExpandedText();
                _input.Clear();
                _showShortcuts = false;
                _suppressSuggestions = null;
                _vim?.Reset();
                Submit(text);
                return;
            case ConsoleKey.Tab:
                if (_suggestions.Count > 0) { AcceptSuggestion(submitIfCommand: false); return; }
                return;
            case ConsoleKey.Backspace: _input.Backspace(); return;
            case ConsoleKey.Delete: _input.Delete(); return;
            case ConsoleKey.LeftArrow: _input.Left(); return;
            case ConsoleKey.RightArrow: _input.Right(); return;
            case ConsoleKey.Home: _input.Home(); return;
            case ConsoleKey.End: _input.End(); return;
            case ConsoleKey.UpArrow:
                if (_suggestions.Count > 0) { _suggestIndex = (_suggestIndex + _suggestions.Count - 1) % _suggestions.Count; return; }
                if (!_input.Up()) _input.HistoryPrev();
                return;
            case ConsoleKey.DownArrow:
                if (_suggestions.Count > 0) { _suggestIndex = (_suggestIndex + 1) % _suggestions.Count; return; }
                if (!_input.Down()) _input.HistoryNext();
                return;
        }

        if (key.KeyChar == '?' && _input.IsEmpty && !burst)
        {
            _showShortcuts = !_showShortcuts;
            return;
        }
        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar) || key.KeyChar == '\t')
        {
            _showShortcuts = false;
            var s = key.KeyChar.ToString();
            _input.Insert(s);
            _suppressSuggestions = null;
        }
    }

    private void NextModal()
    {
        _modal = _modalQueue.TryDequeue(out var next) ? next : null;
    }

    /// <summary>Ctrl+R reverse history search: typing refines, Ctrl+R finds older matches, Enter/Tab/→ accepts
    /// the match into the prompt (without sending it), Esc/Ctrl+G restores the original input.</summary>
    private void HandleSearchKey(ConsoleKeyInfo key, bool ctrl)
    {
        var search = _search!;
        if (ctrl && key.Key == ConsoleKey.R) search.Older();
        else if (key.Key == ConsoleKey.Escape || ctrl && key.Key == ConsoleKey.G)
        {
            _input.SetText(search.Original);
            _search = null;
            return;
        }
        else if (key.Key is ConsoleKey.Enter or ConsoleKey.Tab or ConsoleKey.RightArrow or ConsoleKey.LeftArrow)
        {
            _input.SetText(search.Match ?? search.Original);
            _search = null;
            _suppressSuggestions = _input.Text;
            return;
        }
        else if (key.Key == ConsoleKey.Backspace) search.Backspace();
        else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar)) search.Type(key.KeyChar.ToString());
        _input.SetText(search.Match ?? search.Original);
    }

    private void OpenTranscript()
    {
        _search = null;
        _modal = new TranscriptModal(BuildTranscriptLines(_screen.ContentWidth)) { Height = _screen.Height - 1 };
    }

    /// <summary>The conversation in full detail for the transcript viewer: thinking, every tool input and output.</summary>
    private List<string> BuildTranscriptLines(int w)
    {
        var t = _theme;
        var lines = new List<string>();
        var results = new Dictionary<string, ToolResultPart>();
        foreach (var m in _session.Messages)
            foreach (var r in m.ToolResults) results[r.ToolUseId] = r;
        void Gap() { if (lines.Count > 0) lines.Add(""); }

        foreach (var m in _session.Messages)
        {
            if (m.Role == Role.User)
            {
                if (m.HasToolResults) continue;
                var text = m.Content.OfType<TextPart>().FirstOrDefault()?.Text ?? "";
                if (m.IsMeta)
                {
                    if (text.Length == 0) continue;
                    Gap();
                    foreach (var l in text.Trim().Split('\n').Take(12)) foreach (var wl in TextWidth.Wrap(l.TrimEnd('\r'), w - 4)) lines.Add("  " + t.Faint(wl));
                    continue;
                }
                Gap();
                lines.AddRange(_blocks.User(text, w));
                continue;
            }
            foreach (var th in m.Content.OfType<ThinkingPart>())
                if (th.Text.Trim().Length > 0) { Gap(); lines.AddRange(_blocks.Thinking(th.Text, w, expanded: true)); }
            if (m.Text.Trim().Length > 0) { Gap(); lines.AddRange(_blocks.Assistant(m.Text, w)); }
            foreach (var tu in m.ToolUses)
            {
                Gap();
                var tool = _session.FindTool(tu.Name);
                string display;
                try { display = tool?.DisplayName(tu.Input, _session) ?? tu.Name; } catch { display = tu.Name; }
                results.TryGetValue(tu.Id, out var res);
                lines.Add(_blocks.ToolHeader(display, res is null ? ToolState.Running : res.IsError ? ToolState.Error : ToolState.Success, w));
                foreach (var l in TextWidth.Wrap(Engine.Util.TextUtil.Truncate(tu.Input.GetRawText(), 4000), w - 7))
                    lines.Add("     " + t.Dim(l));
                if (res is null) continue;
                var output = res.TextContent.TrimEnd();
                var outLines = output.Length == 0 ? ["(no output)"] : output.Split('\n');
                var first = true;
                foreach (var l in outLines.Take(500))
                    foreach (var wl in TextWidth.Wrap(l.TrimEnd('\r'), w - 6))
                    {
                        lines.Add(_blocks.ResultPrefix(first) + (res.IsError ? t.C(wl, t.Error) : wl));
                        first = false;
                    }
                if (outLines.Length > 500) lines.Add(_blocks.ResultPrefix(false) + t.Dim($"… +{outLines.Length - 500} lines"));
            }
        }
        // What is still streaming in the current turn.
        if (_stream.Length > 0) { Gap(); lines.AddRange(_blocks.Assistant(_stream.ToString(), w)); }
        return lines;
    }

    // ------------------------------------------------------------------ suggestions (/commands and @files)

    private string? _suppressSuggestions;

    private void UpdateSuggestions()
    {
        var previous = _suggestions.Count > 0 ? _suggestions[Math.Min(_suggestIndex, _suggestions.Count - 1)].Label : null;
        _suggestions = [];
        var text = _input.Text;
        if (_busy && text.Length == 0 || text == _suppressSuggestions) return;

        if (text.StartsWith('/') && !text.Contains(' ') && !text.Contains('\n') && _input.Cursor == text.Length)
        {
            var prefix = text[1..];
            _suggestions = AllCommands()
                .Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || prefix.Length > 1 && c.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(c => c.Name)
                .Select(c => ("/" + c.Name, c.Description, "/" + c.Name + " "))
                .ToList();
        }
        else
        {
            var (token, _) = _input.CurrentToken();
            if (token.StartsWith('@') && token.Length >= 1)
            {
                var q = token[1..].Replace('\\', '/');
                _fileIndex ??= BuildFileIndex();
                _suggestions = _fileIndex
                    .Select(f => (f, score: Score(f, q)))
                    .Where(x => x.score >= 0)
                    .OrderBy(x => x.score).ThenBy(x => x.f.Length)
                    .Take(30)
                    .Select(x => ("@" + x.f, "", "@" + x.f + " "))
                    .ToList();
            }
        }
        if (_suggestions.Count == 0) { _suggestIndex = 0; return; }
        var keep = previous is null ? -1 : _suggestions.FindIndex(s => s.Label == previous);
        _suggestIndex = keep >= 0 ? keep : 0;
    }

    private static int Score(string path, string query)
    {
        if (query.Length == 0) return path.Count(c => c == '/');
        var name = Path.GetFileName(path);
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (path.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (path.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        // Subsequence (fuzzy) match.
        var qi = 0;
        foreach (var c in path) if (qi < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[qi])) qi++;
        return qi == query.Length ? 5 : -1;
    }

    private List<string> BuildFileIndex()
    {
        var list = new List<string>();
        var root = _runtime.Cwd;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".vs", "dist", "build", "__pycache__", ".venv", "target" };
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && list.Count < 10_000)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                    if (!skip.Contains(Path.GetFileName(d)))
                    {
                        stack.Push(d);
                        list.Add(Path.GetRelativePath(root, d).Replace('\\', '/') + "/");
                    }
                foreach (var f in Directory.EnumerateFiles(dir)) list.Add(Path.GetRelativePath(root, f).Replace('\\', '/'));
            }
            catch (Exception) { }
        }
        return list;
    }

    private bool AcceptSuggestion(bool submitIfCommand)
    {
        if (_suggestions.Count == 0) return false;
        var (label, _, insert) = _suggestions[_suggestIndex];
        if (label.StartsWith('/'))
        {
            if (submitIfCommand)
            {
                // Enter on a command runs it immediately (commands needing args just get inserted).
                var name = label[1..];
                var needsArgs = AllCommands().FirstOrDefault(c => c.Name == name).NeedsArgs;
                if (!needsArgs)
                {
                    _input.Clear();
                    _suggestions.Clear();
                    Submit(label);
                    return true;
                }
            }
            _input.SetText(insert);
        }
        else
        {
            var (_, start) = _input.CurrentToken();
            _input.ReplaceToken(start, insert);
        }
        _suggestions.Clear();
        _suppressSuggestions = _input.Text;
        return true;
    }

    private void ShowTodos()
    {
        if (_session.Todos.Count == 0) { Flash(UiText.Current.NoTodos); return; }
        Commit(_blocks.Todos(_session.Todos, _screen.ContentWidth));
    }
}
