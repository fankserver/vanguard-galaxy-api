using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;
namespace CargoRecovery;

/// <summary>
/// Main-thread consumer wiring, independent of the BepInEx loader.
///
/// ISOLATION RULE: this example only ever touches the derelict station it authored itself. It does
/// not register contextual actions on arbitrary observed targets, does not attach content to vanilla
/// encounters, and never takes command control of a boarding operation it did not create. Vanilla
/// boarding, and every other mod's boarding, stays exactly vanilla.
///
/// Its own station is adopted automatically: the cargo layout attaches by installation identity as
/// soon as a live boarding target belongs to that installation, which happens when the player
/// arrives. Until then the API answers StaleTarget, a temporary refusal that is simply retried on
/// the next boarding observation.
/// </summary>
public sealed class CargoAuthorSession : IDisposable
{
    private const string Id = "vgmodapi.example.cargo";
    private readonly List<IDisposable> _leases = new();
    private CargoEncounter? _author;
    private CargoEncounterPanel? _panel;
    private bool _disposed;
    private ILifecycleService? _lifecycle;
    private IDungeonOperationService? _boarding;
    private IDungeonPanelService? _panelService;
    private IDungeonCommandService? _commands;
    private IDungeonTacticalService? _tactics;
    private IDungeonSettlementService? _settlement;
    private Action<BoardingEvent>? _boardingHandler;
    private Action<LifecycleEvent>? _lifecycleHandler;
    private readonly Action<string> _log;
    private readonly Action<string> _warn;

    /// <summary>The registered encounter, or null until content registration succeeds.</summary>
    public CargoEncounter? Encounter => _author;

    /// <summary>Supplied by the plugin so the session can adopt the station this mod authored.</summary>
    public Func<IDungeonInstallation?>? OwnInstallation { get; set; }

    /// <summary>True once the authored layout is attached to the authored station.</summary>
    public bool Attached { get; private set; }

    /// <summary>The one boarding target this example owns, or null.</summary>
    public BoardingHandle? OwnedTarget { get; private set; }

    public CargoAuthorSession(string reward, ILifecycleService? lifecycle, IDungeonOperationService? boarding, IDungeonContentService? content,
        IDungeonPanelService? panel, IDungeonCommandService? commands, IDungeonTacticalService? tactics, IDungeonSettlementService? settlement,
        Action<string> log, Action<string>? warn = null)
    {
        if (log == null) throw new ArgumentNullException(nameof(log));
        _log = log; _warn = warn ?? log;
        if (lifecycle == null || boarding == null || content == null || string.IsNullOrWhiteSpace(reward))
        { _warn("Cargo example requires a configured RewardItemId and available boarding/dungeon content."); return; }
        _panelService = panel; _commands = commands; _tactics = tactics; _settlement = settlement;
        var retried = false;
        void Initialize(bool allowContentAttempt = true)
        {
            if (_disposed || _boardingHandler != null) return;
            if (_author == null)
            {
                if (!allowContentAttempt) return;
                try { _author = new CargoEncounter(content, Id, reward); _log("Cargo content registered; shipment reward = " + reward + "."); }
                catch (ArgumentException error)
                {
                    _warn($"Cargo definition unavailable for RewardItemId '{reward}': {error.Message}. Check native item/crew catalogs; one retry is allowed at gameplay readiness.");
                    return;
                }
            }
            try
            {
                _boarding = boarding;
                // Retry adoption of OUR station on every boarding observation. No vanilla target is
                // inspected, matched by name, or modified here.
                _boardingHandler = fact =>
                {
                    if (OwnedTarget != null && fact.Target.Handle.Equals(OwnedTarget)
                        && (fact.Kind == BoardingEventKind.Retired || fact.Kind == BoardingEventKind.OperationRetired)
                        && !boarding.GetOperations().Any(operation => operation.Target.Equals(OwnedTarget)))
                    {
                        // A removed host can still have living return pods: keep observing settlement
                        // until its last operation retires.
                        ReleaseOwnedTarget();
                        return;
                    }
                    TryAdoptOwnStation();
                };
                boarding.Changed += _boardingHandler;
                TryAdoptOwnStation();
            }
            catch { Dispose(); throw; }
        }
        try
        {
            _lifecycle = lifecycle;
            _lifecycleHandler = fact =>
            {
                if (fact.Kind == LifecycleEventKind.SessionInvalidated) { ReleaseOwnedTarget(); Attached = false; }
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

    /// <summary>
    /// Attaches the authored layout to the authored station once a live target belongs to it, then
    /// wires the extraction/settlement controls for exactly that target and nothing else.
    /// </summary>
    public void TryAdoptOwnStation()
    {
        if (_disposed || _author == null || Attached) return;
        var installation = OwnInstallation?.Invoke();
        if (installation == null) return;

        var result = _author.Attach(installation);
        if (result.Status != DungeonContentStatus.Attached)
        {
            // StaleTarget simply means "not there yet"; it is retried on the next observation.
            if (result.Status != DungeonContentStatus.StaleTarget) _warn("Cargo attach: " + result.Status + " - " + result.Detail);
            return;
        }
        Attached = true;
        _log("Cargo attach: " + result.Status + " - the authored station now uses this mod's layout.");
        BindOwnedTarget(installation);
    }

    /// <summary>
    /// Binds the per-target controls to the one live target of our own installation.
    /// The public API cannot yet resolve an installation to its BoardingHandle, so this adopts the
    /// single target observed at adoption time and verifies it stays ours; it never scans or
    /// modifies unrelated targets.
    /// </summary>
    private void BindOwnedTarget(IDungeonInstallation installation)
    {
        if (_boarding == null || OwnedTarget != null) return;
        var candidates = _boarding.GetTargets();
        if (candidates.Count != 1) return; // Ambiguous: skip the optional controls rather than guess.
        var target = candidates[0].Handle;
        OwnedTarget = target;

        if (_panelService == null || !_panelService.Capabilities.ContextualActions || _commands == null || _tactics == null || _settlement == null)
        { _log("Layout attached; optional contextual control/settlement services unavailable."); return; }

        _panel = new CargoEncounterPanel(Id, target, _panelService, _boarding, _commands, _tactics, _settlement,
            result => _log("Cargo command: " + result.Status),
            result => _log($"Cargo settlement: {result.NativeOutcome}; crew return settled={result.CrewReturnSettled}; observed counts={result.CrewCountsObserved}"));
        _leases.Add(_panel);
    }

    private void ReleaseOwnedTarget()
    {
        var panel = _panel; _panel = null;
        if (panel != null) { _leases.Remove(panel); panel.Dispose(); }
        OwnedTarget = null;
    }

    /// <summary>Forgets the adopted station so a freshly spawned one can be adopted again.</summary>
    public void ResetAdoption() { ReleaseOwnedTarget(); Attached = false; }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_lifecycle != null) _lifecycle.Changed -= _lifecycleHandler;
        _lifecycle = null; _lifecycleHandler = null;
        if (_boarding != null) _boarding.Changed -= _boardingHandler;
        _boarding = null; _boardingHandler = null;
        ReleaseOwnedTarget();
        for (var i = _leases.Count - 1; i >= 0; i--) _leases[i].Dispose();
        _leases.Clear(); _author?.Dispose(); _author = null;
    }
}
