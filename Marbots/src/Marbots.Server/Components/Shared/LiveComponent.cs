using Marbots.Abstractions;
using Microsoft.AspNetCore.Components;

namespace Marbots.Server.Components.Shared;

/// <summary>
/// Base for pages that follow the live event stream. Events are coalesced so a burst of tool calls
/// causes at most one re-render every <see cref="ThrottleMs"/> milliseconds.
/// </summary>
public abstract class LiveComponent : ComponentBase, IDisposable
{
    [Inject] protected IEventBus Bus { get; set; } = default!;
    [Inject] protected Marbots.Server.Services.UiText T { get; set; } = default!;

    private IDisposable? _subscription;
    private int _pending;
    protected virtual int ThrottleMs => 150;

    protected override void OnInitialized()
    {
        _subscription = Bus.Subscribe(OnEvent);
        T.Changed += OnLanguageChanged;
    }

    private void OnLanguageChanged() => _ = InvokeAsync(StateHasChanged);

    /// <summary>Return true when the event should trigger a refresh.</summary>
    protected virtual bool Accept(AgentEvent e) => true;

    /// <summary>Reload data after relevant events. Runs on the renderer's sync context.</summary>
    protected virtual Task RefreshAsync(IReadOnlyList<AgentEvent> events) => Task.CompletedTask;

    private readonly List<AgentEvent> _buffer = [];
    private readonly Lock _lock = new();

    /// <summary>Pages that render streaming text (AssistantDelta) opt in; others ignore these high-frequency events.</summary>
    protected virtual bool WantsDeltas => false;

    private void OnEvent(AgentEvent e)
    {
        if (e.Type == EventTypes.AssistantDelta && !WantsDeltas) return;
        if (!Accept(e)) return;
        lock (_lock) _buffer.Add(e);
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(ThrottleMs);
            Interlocked.Exchange(ref _pending, 0);
            List<AgentEvent> batch;
            lock (_lock) { batch = [.. _buffer]; _buffer.Clear(); }
            try
            {
                await InvokeAsync(async () =>
                {
                    await RefreshAsync(batch);
                    StateHasChanged();
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        });
    }

    public virtual void Dispose()
    {
        _subscription?.Dispose();
        T.Changed -= OnLanguageChanged;
        GC.SuppressFinalize(this);
    }
}
