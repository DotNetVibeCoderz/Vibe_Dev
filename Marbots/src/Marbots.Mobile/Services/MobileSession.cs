using Marbots.Abstractions;
using Marbots.Sdk;

namespace Marbots.Mobile.Services;

/// <summary>
/// The phone's connection to a Marbots server: settings, the SDK client, one reconnecting event stream, the pending
/// approvals and local notifications. Pages subscribe to <see cref="Changed"/> and <see cref="EventReceived"/>.
/// </summary>
public sealed class MobileSession(INotifier notifier) : IAsyncDisposable
{
    private const string UrlKey = "marbots.url";
    private const string ApiKeyKey = "marbots.apikey";
    private const string NotifyKey = "marbots.notify";
    private readonly HashSet<string> _myTasks = [];
    private readonly HashSet<string> _notifiedApprovals = [];
    private CancellationTokenSource? _pump;
    private long _lastEventId;

    public MarbotsClient? Client { get; private set; }
    public string Url { get; private set; } = Preferences.Default.Get(UrlKey, DefaultUrl);
    public bool IsConnected { get; private set; }
    public bool IsReconnecting { get; private set; }
    public string? Error { get; private set; }
    public SystemInfo? Server { get; private set; }
    public IReadOnlyList<BotDefinition> Bots { get; private set; } = [];
    public IReadOnlyList<ApprovalRequest> Pending { get; private set; } = [];
    public List<AgentEvent> Feed { get; } = [];
    public bool SkipApprovals { get; private set; }

    public bool NotificationsEnabled
    {
        get => Preferences.Default.Get(NotifyKey, true);
        set => Preferences.Default.Set(NotifyKey, value);
    }

    /// <summary>The Android emulator reaches the host machine at 10.0.2.2.</summary>
    public static string DefaultUrl => DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.DeviceType == DeviceType.Virtual
        ? "http://10.0.2.2:5170" : "http://localhost:5170";

    public event Action? Changed;
    public event Action<AgentEvent>? EventReceived;

    public BotDefinition? Bot(string id) => Bots.FirstOrDefault(b => b.Id == id);

    public async Task<string?> StoredApiKeyAsync()
    {
        try { return await SecureStorage.Default.GetAsync(ApiKeyKey); }
        catch (Exception) { return null; }
    }

    public Task<bool> StartNotificationsAsync() => notifier.RequestPermissionAsync();

    /// <summary>Connects with the stored settings (on app start).</summary>
    public async Task StartAsync()
    {
        if (NotificationsEnabled) await notifier.RequestPermissionAsync();
        await ConnectAsync(Url, await StoredApiKeyAsync(), save: false);
    }

    public async Task<bool> ConnectAsync(string url, string? apiKey, bool save = true)
    {
        url = url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url + "/", UriKind.Absolute, out var uri))
        {
            Error = "Enter a valid server URL, e.g. http://192.168.1.10:5170";
            Changed?.Invoke();
            return false;
        }
        if (_pump is not null) await _pump.CancelAsync();
        Client?.Dispose();
        Url = url;
        Client = new MarbotsClient(uri, string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim());
        if (save)
        {
            Preferences.Default.Set(UrlKey, url);
            try
            {
                if (string.IsNullOrWhiteSpace(apiKey)) SecureStorage.Default.Remove(ApiKeyKey);
                else await SecureStorage.Default.SetAsync(ApiKeyKey, apiKey.Trim());
            }
            catch (Exception) { /* secure storage unavailable (e.g. unpackaged desktop): key kept in memory only */ }
        }
        try
        {
            Server = await Client.SystemAsync();
            IsConnected = true;
            Error = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or MarbotsApiException or TaskCanceledException)
        {
            IsConnected = false;
            Error = ex.Message;
            Changed?.Invoke();
            return false;
        }
        await RefreshAsync();
        _pump = new CancellationTokenSource();
        _ = PumpAsync(Client, _pump.Token);
        Changed?.Invoke();
        return true;
    }

    public async Task RefreshAsync()
    {
        if (Client is not { } c) return;
        try
        {
            Bots = (await c.Bots.ListAsync()).Where(b => b.Status != BotStatus.Archived)
                .OrderBy(b => b.Id == WellKnown.BossManId ? 0 : 1).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
            await RefreshApprovalsAsync();
            SkipApprovals = await c.Approvals.GetSkipApprovalsAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or MarbotsApiException) { Error = ex.Message; }
        Changed?.Invoke();
    }

    private async Task RefreshApprovalsAsync()
    {
        if (Client is not { } c) return;
        Pending = (await c.Approvals.PendingAsync()).OrderBy(a => a.CreatedAt).ToList();
        foreach (var a in Pending.Where(a => _notifiedApprovals.Add(a.Id)))
            Notify($"{Bot(a.BotId)?.Name ?? a.BotId} needs your approval", $"{a.ToolName}: {a.Reason}");
    }

    /// <summary>Remembers tasks started from this phone so their completion can be notified.</summary>
    public void Track(string taskId) => _myTasks.Add(taskId);

    public async Task ResolveAsync(string approvalId, bool approve, bool forThread = false)
    {
        if (Client is not { } c) return;
        if (approve) await c.Approvals.ApproveAsync(approvalId, forThread ? ApprovalScope.Session : ApprovalScope.Once);
        else await c.Approvals.RejectAsync(approvalId);
        await RefreshApprovalsAsync();
        Changed?.Invoke();
    }

    private async Task PumpAsync(MarbotsClient client, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var e in client.Events.StreamAsync(null, _lastEventId > 0 ? _lastEventId : null, ct))
                {
                    if (e.Id > 0) _lastEventId = e.Id;
                    if (IsReconnecting) { IsReconnecting = false; MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke()); }
                    delay = TimeSpan.FromSeconds(1);
                    MainThread.BeginInvokeOnMainThread(() => _ = HandleAsync(e));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or MarbotsApiException or OperationCanceledException) { Error = ex.Message; }
            IsReconnecting = true;
            MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 20));
        }
    }

    private async Task HandleAsync(AgentEvent e)
    {
        EventReceived?.Invoke(e);
        if (e.Type == EventTypes.AssistantDelta) return;
        if (e.BotId is not null && e.Type is EventTypes.ToolCallStarted or EventTypes.TaskDelegated or EventTypes.ApprovalRequested
                or EventTypes.TaskCreated or EventTypes.TaskStateChanged or EventTypes.AgentThinkingStarted)
        {
            Feed.Insert(0, e);
            if (Feed.Count > 80) Feed.RemoveAt(Feed.Count - 1);
        }
        switch (e.Type)
        {
            case EventTypes.ApprovalRequested or EventTypes.ApprovalResolved:
                try { await RefreshApprovalsAsync(); } catch (Exception ex) when (ex is HttpRequestException or MarbotsApiException) { }
                break;
            case EventTypes.BotCreated or EventTypes.BotDeleted or EventTypes.BotUpdated or EventTypes.SettingsChanged:
                await RefreshAsync();
                return;
            case EventTypes.TaskStateChanged when e.TaskId is not null && _myTasks.Remove(e.TaskId) && e.Data is "Completed" or "Failed":
                Notify($"{Bot(e.BotId ?? "")?.Name ?? "Your bot"} {(e.Data == "Completed" ? "finished" : "failed")}", Preview(e.Message, 120));
                break;
        }
        Changed?.Invoke();
    }

    private void Notify(string title, string body)
    {
        if (NotificationsEnabled) notifier.Show(title, body);
    }

    public static string Preview(string? s, int n)
    {
        s = (s ?? "").ReplaceLineEndings(" ").Trim();
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }

    public async ValueTask DisposeAsync()
    {
        if (_pump is not null) await _pump.CancelAsync();
        Client?.Dispose();
    }
}
