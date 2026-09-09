using System;
using System.Collections.Generic;
using System.Linq;
using AuthoredDungeon;
using VGModAPI;
namespace DungeonAuthor;

/// <summary>Main-thread consumer wiring, independent of the BepInEx loader.</summary>
public sealed class CargoAuthorSession : IDisposable
{
    private const string Id = "vgmodapi.example.cargo";
    private readonly List<IDisposable> _leases = new();
    private readonly Dictionary<BoardingHandle, CargoRecoveryPanel> _panels = new();
    private readonly HashSet<BoardingHandle> _retiredTargets = new();
    private CargoRecovery? _author;
    private bool _disposed;
    private ILifecycleService? _lifecycle;
    private IDungeonOperationService? _boarding;
    private Action<BoardingEvent>? _boardingHandler;
    private Action<LifecycleEvent>? _lifecycleHandler;
    public CargoAuthorSession(string reward, ILifecycleService? lifecycle, IDungeonOperationService? boarding, IDungeonContentService? content,
        IDungeonPanelService? panel, IDungeonCommandService? commands, IDungeonTacticalService? tactics, IDungeonSettlementService? settlement,
        Action<string> log, Action<string>? warn = null)
    {
        if (log == null) throw new ArgumentNullException(nameof(log));
        warn ??= log;
        if (lifecycle == null || boarding == null || content == null || string.IsNullOrWhiteSpace(reward))
        { warn("Cargo example requires configured RewardItemId and available boarding/dungeon content."); return; }
        var retried = false;
        void Initialize(bool allowContentAttempt = true)
        {
            if (_disposed || _boardingHandler != null) return;
            if (_author == null)
            {
                if (!allowContentAttempt) return;
                try { _author = new CargoRecovery(content, Id, reward); }
                catch (ArgumentException error)
                {
                    warn($"Cargo definition unavailable for RewardItemId '{reward}': {error.Message}. Check native item/crew catalogs; one retry is allowed at gameplay readiness.");
                    return;
                }
            }
            try
            {
                if (panel == null || !panel.Capabilities.ContextualActions || commands == null || tactics == null || settlement == null)
                { warn("Content registered; optional contextual control/settlement services unavailable."); return; }
                _leases.Add(panel.RegisterAction(Id, "attach-cargo", view => view.Operation == null
                    ? new DungeonPanelAction("Attach cargo encounter", "Explicitly attach cargo content to this observed target. Existing attachments are never replaced.") : null,
                    view => log("Cargo attach: " + _author.Attach(view.Target.Handle).Status)));
                void Track(BoardingOperationSnapshot operation)
                {
                    if (_panels.ContainsKey(operation.Target)) return;
                    _panels.Add(operation.Target, new CargoRecoveryPanel(Id, operation.Target, panel, boarding, commands, tactics, settlement,
                        result => log("Cargo command: " + result.Status),
                        result => log($"Cargo settlement: {result.NativeOutcome}; crew return settled={result.CrewReturnSettled}; observed counts={result.CrewCountsObserved}")));
                }
                _boarding = boarding;
                _boardingHandler = fact =>
                {
                    if (fact.Kind == BoardingEventKind.Retired) _retiredTargets.Add(fact.Target.Handle);
                    if ((fact.Kind == BoardingEventKind.Retired || fact.Kind == BoardingEventKind.OperationRetired) && _retiredTargets.Contains(fact.Target.Handle))
                    {
                        // A removed host can still have living return pods. Keep observing until its last operation retires.
                        if (!boarding.GetOperations().Any(operation => operation.Target.Equals(fact.Target.Handle)))
                        {
                            if (_panels.TryGetValue(fact.Target.Handle, out var stale)) { stale.Dispose(); _panels.Remove(fact.Target.Handle); }
                            _retiredTargets.Remove(fact.Target.Handle);
                        }
                        return;
                    }
                    if (fact.Kind != BoardingEventKind.OperationRetired && fact.Operation != null) Track(fact.Operation);
                };
                boarding.Changed += _boardingHandler;
                foreach (var operation in boarding.GetOperations()) Track(operation);
            }
            catch { Dispose(); throw; }
        }
        try
        {
            _lifecycle = lifecycle;
            _lifecycleHandler = fact =>
            {
                if (fact.Kind == LifecycleEventKind.SessionInvalidated) ClearTargets();
                if (fact.Kind == LifecycleEventKind.GameplayInitialized)
                {
                    // Content and optional presentation can become ready independently.
                    var allowContentAttempt = !retried; retried = true;
                    Initialize(allowContentAttempt);
                }
            };
            lifecycle.Changed += _lifecycleHandler;
            Initialize();
        }
        catch { Dispose(); throw; }
    }
    private void ClearTargets()
    { foreach (var panel in _panels.Values.ToArray()) panel.Dispose(); _panels.Clear(); _retiredTargets.Clear(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_lifecycle != null) _lifecycle.Changed -= _lifecycleHandler;
        _lifecycle = null; _lifecycleHandler = null;
        if (_boarding != null) _boarding.Changed -= _boardingHandler;
        _boarding = null; _boardingHandler = null;
        ClearTargets();
        for (var i = _leases.Count - 1; i >= 0; i--) _leases[i].Dispose();
        _leases.Clear(); _author?.Dispose(); _author = null;
    }
}
