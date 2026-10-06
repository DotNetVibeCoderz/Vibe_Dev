using System.Numerics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Marbots.Abstractions;
using Marbots.Desktop.Office;
using Marbots.Desktop.Services;
using Marbots.Sdk;
using ThreeNet;
using ThreeNet.Avalonia;
using Key = Avalonia.Input.Key;
using KeyModifiers = Avalonia.Input.KeyModifiers;
using Path = System.IO.Path;

namespace Marbots.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MarbotsConnection _connection = new();
    private readonly OfficeDirector _director = new();
    private readonly OfficeScene _office = new();
    private readonly Dictionary<string, BotDefinition> _bots = [];
    private readonly Dictionary<string, Border> _tags = [];
    private readonly Dictionary<string, Border> _zoneTags = [];
    private readonly List<AgentEvent> _feed = [];
    private readonly StartupOptions _options;
    private OrbitController? _orbit;
    private string _page = "office";
    private string? _chatBotId;
    private string? _threadId;
    private string? _liveTaskId;
    private SelectableTextBlock? _liveText;
    private readonly StringBuilder _live = new();
    private int _frames;
    private double _fpsClock;

    public MainWindow() : this([]) { }

    public MainWindow(string[] args)
    {
        InitializeComponent();
        _options = StartupOptions.Parse(args);

        Viewport.Scene = _office.Scene;
        Viewport.Camera = _office.Camera;
        Viewport.RendererOptions = RendererOptions.Default with
        {
            BgraOutput = true,
            MsaaSamples = 4,
            ToneMapping = ToneMapping.Aces,
            Exposure = 1.0f,
            Shadows = true,
            ShadowMapSize = 2048,
            Ssao = true,
            Bloom = true,
            BloomIntensity = 0.35f,
            BloomThreshold = 1.2f,
        };
        Viewport.MaxFramesPerSecond = 60;
        Viewport.Frame += OnFrame;
        Viewport.RenderFailed += (_, message) =>
        {
            RenderError.IsVisible = true;
            RenderErrorText.Text = "The 3D view could not start on this machine: " + message + "\nChat and approvals still work.";
        };
        _orbit = new OrbitController(Viewport, _office.Camera) { MinDistance = 6f, MaxDistance = 48f };
        SetCamera("overview");

        NavOffice.Click += (_, _) => ShowPage("office");
        NavChat.Click += (_, _) => ShowPage("chat");
        NavApprovals.Click += (_, _) => ShowPage("approvals");
        NavConnect.Click += (_, _) => ShowPage("connect");
        CamOverview.Click += (_, _) => SetCamera("overview");
        CamDesks.Click += (_, _) => SetCamera("desks");
        CamStations.Click += (_, _) => SetCamera("stations");
        CamFront.Click += (_, _) => SetCamera("front");
        AutoOrbit.IsCheckedChanged += (_, _) => { if (_orbit is not null) { _orbit.AutoRotate = AutoOrbit.IsChecked == true; _orbit.AutoRotateSpeed = 0.08f; } };

        BotList.SelectionChanged += async (_, _) =>
        {
            if (BotList.SelectedItem is ListBoxItem { Tag: string id } && id != _chatBotId) await OpenChatAsync(id, null);
        };
        NewThread.Click += async (_, _) => { if (_chatBotId is not null) await OpenChatAsync(_chatBotId, newThread: true); };
        SendButton.Click += async (_, _) => await SendAsync();
        Composer.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                await SendAsync();
            }
        };
        ConnectButton.Click += async (_, _) => await ConnectAsync(startLocal: false);
        StartLocalButton.Click += async (_, _) => await ConnectAsync(startLocal: true);

        _connection.EventReceived += e => Dispatcher.UIThread.Post(() => OnEvent(e));
        _connection.StateChanged += s => Dispatcher.UIThread.Post(() => ShowConnection(s));

        var (url, key) = MarbotsConnection.FromEnvironment();
        UrlBox.Text = (_options.Url ?? url).ToString().TrimEnd('/');
        KeyBox.Text = _options.ApiKey ?? key;
        Opened += async (_, _) =>
        {
            ShowPage(_options.Page ?? "office");
            await ConnectAsync(_options.StartLocal);
            if (_options.ChatBot is not null) await OpenChatAsync(_options.ChatBot, null);
            if (_options.Screenshot is not null) _ = CaptureAsync(_options.Screenshot, _options.ScreenshotDelay);
        };
        Closing += async (_, _) => await _connection.DisposeAsync();
        Closed += (_, _) =>
        {
            _orbit?.Dispose();
            _office.Dispose();
        };
    }

    // ---------------- connection ----------------

    private async Task ConnectAsync(bool startLocal)
    {
        if (!Uri.TryCreate((UrlBox.Text ?? "").Trim().TrimEnd('/') + "/", UriKind.Absolute, out var url))
        {
            ConnectStatus.Text = "Enter a valid server URL.";
            return;
        }
        var key = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim();
        ConnectStatus.Text = $"Connecting to {url}…";
        var ok = await _connection.ConnectAsync(url, key);
        if (!ok && startLocal)
        {
            ConnectStatus.Text = "Starting a local Marbots server…";
            ok = await _connection.StartLocalServerAsync(url) && await _connection.ConnectAsync(url, key);
        }
        if (!ok)
        {
            ConnectStatus.Text = $"Could not connect: {_connection.LastError}";
            if (_page != "connect") ShowPage("connect");
            return;
        }
        var info = _connection.System!;
        ConnectStatus.Text = $"Connected to {url}" + (_connection.OwnsLocalServer ? " (local server started by this app)" : "");
        ServerInfo.Text = $"{info.Product} {info.Version} · data in {info.DataDirectory}\n{info.CreditsEn}";
        await ReloadBotsAsync();
        await ReloadApprovalsAsync();
        try { DangerBanner.IsVisible = await _connection.Client!.Approvals.GetSkipApprovalsAsync(); }
        catch (MarbotsApiException) { }
    }

    private void ShowConnection(ConnectionState state)
    {
        var (brush, text) = state switch
        {
            ConnectionState.Connected => ((IBrush)this.FindResource("Ok")!, $"Connected · {_connection.BaseAddress.Authority}"),
            ConnectionState.Connecting => ((IBrush)this.FindResource("Warn")!, "Reconnecting…"),
            _ => ((IBrush)this.FindResource("Danger")!, "Offline"),
        };
        StatusDot.Fill = brush;
        HeaderDot.Fill = brush;
        ConnectionText.Text = text;
    }

    // ---------------- pages ----------------

    private void ShowPage(string page)
    {
        _page = page;
        OfficePage.IsVisible = page == "office";
        ChatPage.IsVisible = page == "chat";
        ApprovalsPage.IsVisible = page == "approvals";
        ConnectPage.IsVisible = page == "connect";
        Viewport.IsRendering = page == "office";
        foreach (var (button, name) in new[] { (NavOffice, "office"), (NavChat, "chat"), (NavApprovals, "approvals"), (NavConnect, "connect") })
            button.Classes.Set("active", name == page);
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "office" => ("Office", "Your team at work, live in 3D"),
            "chat" => ("Chat", "Talk to any bot; replies stream as they are written"),
            "approvals" => ("Approvals", "Risky actions wait here for you"),
            _ => ("Server", "Connect to a Marbots server or start one on this computer"),
        };
        if (page == "chat" && _chatBotId is null && _bots.Count > 0) _ = OpenChatAsync(WellKnown.BossManId, null);
    }

    // ---------------- events ----------------

    private void OnEvent(AgentEvent e)
    {
        _director.Apply(e);
        switch (e.Type)
        {
            case EventTypes.BotCreated or EventTypes.BotDeleted or EventTypes.BotUpdated or EventTypes.BotStateChanged:
                _ = ReloadBotsAsync();
                break;
            case EventTypes.ApprovalRequested or EventTypes.ApprovalResolved:
                _ = ReloadApprovalsAsync();
                break;
            case EventTypes.SettingsChanged:
                _ = RefreshDangerAsync();
                break;
        }
        if (e.Type != EventTypes.AssistantDelta && e.Type is not (EventTypes.TaskProgressed or EventTypes.AgentThinkingCompleted or EventTypes.ToolCallCompleted))
            AddFeed(e);
        if (e.ThreadId is not null && e.ThreadId == _threadId) OnChatEvent(e);
    }

    private async Task RefreshDangerAsync()
    {
        try { DangerBanner.IsVisible = await _connection.Client!.Approvals.GetSkipApprovalsAsync(); }
        catch (Exception ex) when (ex is MarbotsApiException or HttpRequestException) { }
    }

    private void AddFeed(AgentEvent e)
    {
        if (e.BotId is null) return;
        _feed.Insert(0, e);
        if (_feed.Count > 9) _feed.RemoveAt(_feed.Count - 1);
        Feed.Children.Clear();
        foreach (var f in _feed)
        {
            var name = _bots.TryGetValue(f.BotId!, out var b) ? b.Name : f.BotId!;
            var row = new StackPanel { Spacing = 1 };
            row.Children.Add(new TextBlock
            {
                FontSize = 11,
                Inlines =
                [
                    new Avalonia.Controls.Documents.Run(f.Timestamp.ToLocalTime().ToString("HH:mm:ss") + "  ") { Foreground = (IBrush)this.FindResource("Muted")! },
                    new Avalonia.Controls.Documents.Run(name) { FontWeight = FontWeight.SemiBold, Foreground = BotBrush(f.BotId!) },
                    new Avalonia.Controls.Documents.Run("  " + Describe(f)),
                ],
                TextWrapping = TextWrapping.Wrap,
            });
            Feed.Children.Add(row);
        }
    }

    private static string Describe(AgentEvent e) => e.Type switch
    {
        EventTypes.ToolCallStarted => "uses " + Preview(e.Message?.Split(' ', 2)[0], 40),
        EventTypes.AgentThinkingStarted => "is thinking",
        EventTypes.ApprovalRequested => "needs approval: " + Preview(e.Message, 50),
        EventTypes.TaskDelegated => "got a task: " + Preview(e.Message, 50),
        EventTypes.TaskStateChanged => e.Data?.ToLowerInvariant() ?? "task updated",
        EventTypes.TaskCreated => "started: " + Preview(e.Message, 50),
        EventTypes.MessageAdded => "posted a message",
        _ => Preview(e.Message ?? e.Type, 60),
    };

    private static string Preview(string? s, int n)
    {
        s = (s ?? "").ReplaceLineEndings(" ");
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }

    // ---------------- office ----------------

    private async Task ReloadBotsAsync()
    {
        if (_connection.Client is not { } client) return;
        List<BotDefinition> bots;
        try { bots = await client.Bots.ListAsync(); }
        catch (Exception ex) when (ex is MarbotsApiException or HttpRequestException) { return; }
        _bots.Clear();
        foreach (var b in bots.Where(b => b.Status != BotStatus.Archived)) _bots[b.Id] = b;
        _director.SetBots(_bots.Values);
        RebuildRoster();
        RebuildBotList();
    }

    private void RebuildRoster()
    {
        Roster.Children.Clear();
        foreach (var b in OrderedBots())
        {
            var row = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = b.Id,
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            grid.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = BotBrush(b.Id), VerticalAlignment = VerticalAlignment.Center });
            var name = new TextBlock { Text = b.Name, Margin = new Thickness(8, 0), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);
            var status = new TextBlock { FontSize = 11, Foreground = (IBrush)this.FindResource("Muted")!, VerticalAlignment = VerticalAlignment.Center, Name = "status" };
            Grid.SetColumn(status, 2);
            grid.Children.Add(status);
            row.Content = grid;
            row.Click += async (_, _) =>
            {
                ShowPage("chat");
                await OpenChatAsync(b.Id, null);
            };
            ToolTip.SetTip(row, $"{b.Role} · open chat");
            Roster.Children.Add(row);
        }
    }

    private IEnumerable<BotDefinition> OrderedBots() =>
        _bots.Values.OrderBy(b => b.Id == WellKnown.BossManId ? 0 : 1).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase);

    private void OnFrame(object? sender, FrameEventArgs e)
    {
        var dt = MathF.Min(e.DeltaSeconds, 0.05f);
        _director.Update(dt);
        _office.Update(_director, _bots, dt);
        UpdateTags();
        UpdateRosterStatus();

        _frames++;
        _fpsClock += dt;
        if (_fpsClock >= 0.5)
        {
            FpsText.Text = $"{_frames / _fpsClock:0} fps";
            _frames = 0;
            _fpsClock = 0;
        }
    }

    private void UpdateRosterStatus()
    {
        var busy = 0;
        foreach (var child in Roster.Children)
        {
            if (child is not Button { Tag: string id, Content: Grid grid }) continue;
            var agent = _director.Find(id);
            var label = agent is null ? "" : agent.IsMoving ? "walking" : agent.Label;
            if (agent is not null && agent.Activity != BotActivity.Idle) busy++;
            if (grid.Children[2] is TextBlock t && t.Text != label) t.Text = label;
        }
        var text = $"{busy} busy · {_bots.Count} bots";
        if (BusyText.Text != text) BusyText.Text = text;
    }

    /// <summary>Name tags follow the robots: world position projected with the camera's matrices.</summary>
    private void UpdateTags()
    {
        var size = TagLayer.Bounds.Size;
        if (size.Width < 1 || size.Height < 1) return;
        Matrix4x4.Invert(_office.Camera.WorldMatrix, out var view);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(OfficeScene.FieldOfView, (float)(size.Width / size.Height), 0.2f, 200f);
        var viewProjection = view * projection;
        var seen = new HashSet<string>();
        foreach (var agent in _director.Agents)
        {
            seen.Add(agent.Id);
            if (!_tags.TryGetValue(agent.Id, out var tag))
            {
                tag = new Border { Classes = { "tag" }, BorderBrush = BotBrush(agent.Id), Child = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold } };
                _tags[agent.Id] = tag;
                TagLayer.Children.Add(tag);
            }
            var clip = Vector4.Transform(new Vector4(agent.Position.X, agent.Id == WellKnown.BossManId ? 1.75f : 1.6f, agent.Position.Y, 1f), viewProjection);
            if (clip.W <= 0.01f)
            {
                tag.IsVisible = false;
                continue;
            }
            var x = (clip.X / clip.W + 1) / 2 * size.Width;
            var y = (1 - clip.Y / clip.W) / 2 * size.Height;
            tag.IsVisible = x > -50 && x < size.Width + 50 && y > -20 && y < size.Height + 20;
            var text = agent.Name + (agent.Activity is BotActivity.Idle or BotActivity.Walking ? "" : " · " + agent.Label);
            if (tag.Child is TextBlock tb && tb.Text != text) tb.Text = text;
            Canvas.SetLeft(tag, x - tag.Bounds.Width / 2);
            Canvas.SetTop(tag, y - tag.Bounds.Height);
        }
        foreach (var zone in OfficeLayout.Zones)
        {
            if (!_zoneTags.TryGetValue(zone.Id, out var label))
            {
                var c = zone.Color;
                label = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x13, 0x26)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(7, 2),
                    Child = new TextBlock { Text = zone.Label, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb((byte)(80 + c.X * 175), (byte)(80 + c.Y * 175), (byte)(80 + c.Z * 175))) },
                };
                _zoneTags[zone.Id] = label;
                TagLayer.Children.Insert(0, label);
            }
            var front = zone.Center.Y > 0 ? zone.Center.Y + zone.Size.Y / 2 - 0.3f : zone.Center.Y + zone.Size.Y / 2 - 0.3f;
            var p = Vector4.Transform(new Vector4(zone.Center.X, 0.02f, front, 1f), viewProjection);
            label.IsVisible = p.W > 0.01f;
            if (!label.IsVisible) continue;
            Canvas.SetLeft(label, (p.X / p.W + 1) / 2 * size.Width - label.Bounds.Width / 2);
            Canvas.SetTop(label, (1 - p.Y / p.W) / 2 * size.Height - label.Bounds.Height / 2);
        }
        foreach (var gone in _tags.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            TagLayer.Children.Remove(_tags[gone]);
            _tags.Remove(gone);
        }
    }

    private void SetCamera(string preset)
    {
        if (_orbit is null) return;
        (_orbit.Target, _orbit.Distance, _orbit.Yaw, _orbit.Pitch) = preset switch
        {
            "desks" => (new Vector3(0f, 0.6f, 0f), 13f, 0f, 0.62f),
            "stations" => (new Vector3(0f, 0.8f, -7f), 15f, 0f, 0.5f),
            "front" => (new Vector3(0f, 0.6f, 6.5f), 14f, MathF.PI, 0.55f),
            _ => (new Vector3(0f, 0f, 0.5f), 31f, 0f, 0.82f),
        };
        _orbit.Apply();
    }

    private IBrush BotBrush(string botId)
    {
        var c = OfficeScene.ParseColor(_bots.TryGetValue(botId, out var b) ? b.Color : "#6D7BFF");
        return new SolidColorBrush(Color.FromRgb((byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255)));
    }

    // ---------------- chat ----------------

    private void RebuildBotList()
    {
        var selected = _chatBotId;
        BotList.Items.Clear();
        foreach (var b in OrderedBots())
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            panel.Children.Add(Avatar(b, 30));
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = b.Name, FontWeight = FontWeight.SemiBold });
            text.Children.Add(new TextBlock { Text = b.Role, FontSize = 11, Foreground = (IBrush)this.FindResource("Muted")!, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 });
            panel.Children.Add(text);
            var item = new ListBoxItem { Content = panel, Tag = b.Id };
            BotList.Items.Add(item);
            if (b.Id == selected) BotList.SelectedItem = item;
        }
    }

    private Border Avatar(BotDefinition b, double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = BotBrush(b.Id),
        Child = new TextBlock { Text = Initials(b.Name), FontSize = size * 0.38, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch { 0 => "?", 1 => parts[0][..1].ToUpperInvariant(), _ => (parts[0][..1] + parts[1][..1]).ToUpperInvariant() };
    }

    private async Task OpenChatAsync(string botId, bool? newThread)
    {
        if (_connection.Client is not { } client || !_bots.TryGetValue(botId, out var bot)) return;
        _chatBotId = botId;
        foreach (var item in BotList.Items.OfType<ListBoxItem>())
            if (item.Tag as string == botId) BotList.SelectedItem = item;
        ChatTitle.Text = bot.Name;
        ChatAvatar.Background = BotBrush(bot.Id);
        ChatAvatarText.Text = Initials(bot.Name);
        try
        {
            var model = await client.Bots.GetModelAsync(bot.Id);
            ChatSubtitle.Text = $"{bot.Role} · {model.Effective}";
        }
        catch (MarbotsApiException) { ChatSubtitle.Text = bot.Role; }
        try
        {
            ChatThread? thread = null;
            if (newThread != true) thread = (await client.Threads.ListAsync(bot.Id)).OrderByDescending(t => t.UpdatedAt).FirstOrDefault();
            thread ??= await client.Threads.CreateAsync(bot.Id, null);
            _threadId = thread.Id;
            await LoadMessagesAsync();
        }
        catch (Exception ex) when (ex is MarbotsApiException or HttpRequestException)
        {
            Messages.Children.Clear();
            Messages.Children.Add(new TextBlock { Text = ex.Message, Foreground = (IBrush)this.FindResource("Danger")! });
        }
    }

    private async Task LoadMessagesAsync()
    {
        if (_connection.Client is not { } client || _threadId is null) return;
        var messages = await client.Threads.MessagesAsync(_threadId);
        Messages.Children.Clear();
        _liveText = null;
        foreach (var m in messages.Where(m => m.Role is "user" or "assistant" && m.ToolCalls is null && !string.IsNullOrWhiteSpace(m.Content)).TakeLast(60))
            Messages.Children.Add(Bubble(m.Role == "user", m.Content, m.CreatedAt));
        if (messages.Count == 0)
            Messages.Children.Add(new TextBlock { Text = "Say hello. Ask for research, documents, code, or a plan for the whole team.", Foreground = (IBrush)this.FindResource("Muted")! });
        MessageScroll.ScrollToEnd();
    }

    private Control Bubble(bool user, string text, DateTimeOffset at)
    {
        var body = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21 };
        var border = new Border
        {
            Background = user ? new SolidColorBrush(Color.Parse("#2A3170")) : (IBrush)this.FindResource("Surface2")!,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10),
            MaxWidth = 720,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = body,
        };
        ToolTip.SetTip(border, at.ToLocalTime().ToString("dd MMM HH:mm"));
        return border;
    }

    private async Task SendAsync()
    {
        var text = Composer.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _connection.Client is not { } client || _threadId is null) return;
        Composer.Text = "";
        SendButton.IsEnabled = false;
        Messages.Children.Add(Bubble(true, text, DateTimeOffset.Now));
        MessageScroll.ScrollToEnd();
        try
        {
            var sent = await client.Threads.SendAsync(_threadId, text);
            _liveTaskId = sent.Task.Id;
            _live.Clear();
            var bubble = (Border)Bubble(false, "…", DateTimeOffset.Now);
            _liveText = (SelectableTextBlock)bubble.Child!;
            Messages.Children.Add(bubble);
            MessageScroll.ScrollToEnd();
        }
        catch (Exception ex) when (ex is MarbotsApiException or HttpRequestException)
        {
            Messages.Children.Add(new TextBlock { Text = ex.Message, Foreground = (IBrush)this.FindResource("Danger")! });
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    private void OnChatEvent(AgentEvent e)
    {
        // Messages sent from another client (web, CLI, a channel) show up and stream here too.
        if (_liveTaskId is null)
        {
            if (e.Type == EventTypes.MessageAdded) _ = LoadMessagesAsync();
            if (e.Type != EventTypes.AssistantDelta || e.BotId != _chatBotId) return;
            _liveTaskId = e.TaskId;
            _live.Clear();
            var bubble = (Border)Bubble(false, "", DateTimeOffset.Now);
            _liveText = (SelectableTextBlock)bubble.Child!;
            Messages.Children.Add(bubble);
        }
        if (e.TaskId != _liveTaskId || _liveText is null) return;
        switch (e.Type)
        {
            case EventTypes.AssistantDelta:
                _live.Append(e.Message);
                _liveText.Text = _live + " ▍";
                MessageScroll.ScrollToEnd();
                break;
            case EventTypes.ToolCallStarted:
                // Text before a tool call was a preamble; the final answer streams after it.
                _live.Clear();
                _liveText.Text = "Working: " + Preview(e.Message, 80);
                break;
            case EventTypes.ApprovalRequested:
                _liveText.Text = "Waiting for your approval: " + Preview(e.Message, 80);
                break;
            case EventTypes.TaskStateChanged when e.Data is "Completed" or "Failed" or "Cancelled":
                _liveTaskId = null;
                _ = LoadMessagesAsync();
                break;
        }
    }

    // ---------------- approvals ----------------

    private async Task ReloadApprovalsAsync()
    {
        if (_connection.Client is not { } client) return;
        List<ApprovalRequest> pending;
        try { pending = await client.Approvals.PendingAsync(); }
        catch (Exception ex) when (ex is MarbotsApiException or HttpRequestException) { return; }
        ApprovalBadge.IsVisible = pending.Count > 0;
        ApprovalBadgeText.Text = pending.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        NoApprovals.IsVisible = pending.Count == 0;
        ApprovalList.Children.Clear();
        foreach (var a in pending.OrderBy(a => a.CreatedAt))
        {
            var name = _bots.TryGetValue(a.BotId, out var b) ? b.Name : a.BotId;
            var card = new StackPanel { Spacing = 8 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            if (b is not null) head.Children.Add(Avatar(b, 28));
            head.Children.Add(new TextBlock { Text = $"{name} wants to use {a.ToolName}", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new Border { Classes = { "pill" }, Child = new TextBlock { Text = $"{a.Risk} risk · {a.Category}", FontSize = 11 } });
            card.Children.Add(head);
            if (!string.IsNullOrWhiteSpace(a.Reason)) card.Children.Add(new TextBlock { Text = a.Reason, Foreground = (IBrush)this.FindResource("Muted")!, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(new Border
            {
                Background = (IBrush)this.FindResource("Bg")!,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8),
                Child = new SelectableTextBlock { Text = a.Summary ?? Preview(a.Arguments, 900), FontFamily = a.Summary is null ? new FontFamily("Cascadia Mono, Consolas, monospace") : FontFamily.Default, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var approve = new Button { Content = "Approve", Classes = { "primary" } };
            var session = new Button { Content = "Approve for this thread" };
            var reject = new Button { Content = "Reject", Classes = { "danger" } };
            approve.Click += async (_, _) => await ResolveAsync(() => client.Approvals.ApproveAsync(a.Id));
            session.Click += async (_, _) => await ResolveAsync(() => client.Approvals.ApproveAsync(a.Id, ApprovalScope.Session));
            reject.Click += async (_, _) => await ResolveAsync(() => client.Approvals.RejectAsync(a.Id));
            buttons.Children.Add(approve);
            buttons.Children.Add(session);
            buttons.Children.Add(reject);
            card.Children.Add(buttons);
            ApprovalList.Children.Add(new Border { Classes = { "panel" }, Child = card });
        }
    }

    private async Task ResolveAsync(Func<Task<ApprovalRequest>> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is MarbotsApiException or HttpRequestException) { ConnectStatus.Text = ex.Message; }
        await ReloadApprovalsAsync();
    }

    // ---------------- screenshots (docs automation) ----------------

    private async Task CaptureAsync(string path, double delaySeconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
        bitmap.Render(this);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path);
        if (_options.ExitAfterScreenshot) Close();
    }
}

/// <summary>Command line: --url, --api-key, --start-local, --page, --chat &lt;bot&gt;, --screenshot &lt;png&gt; [--delay s] [--exit].</summary>
internal sealed record StartupOptions(Uri? Url, string? ApiKey, bool StartLocal, string? Page, string? ChatBot, string? Screenshot, double ScreenshotDelay, bool ExitAfterScreenshot)
{
    public static StartupOptions Parse(string[] args)
    {
        string? Value(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        return new StartupOptions(
            Value("--url") is { } u ? new Uri(u.TrimEnd('/') + "/") : null,
            Value("--api-key"),
            args.Contains("--start-local"),
            Value("--page"),
            Value("--chat"),
            Value("--screenshot"),
            double.TryParse(Value("--delay"), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 6,
            args.Contains("--exit"));
    }
}
