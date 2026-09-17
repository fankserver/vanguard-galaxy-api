using System;
using BepInEx;
using UnityEngine;
using VGModAPI;
namespace CargoRecovery;

/// <summary>
/// Sample/test mod demonstrating authored boarding content end to end. It supplies its own target:
/// one HUD button spawns an owned derelict station in the system you are already in, the cargo layout
/// attaches to it, and a second button removes it again, leaving the system as it was found.
///
/// It still works as a general contextual control for vanilla encounters — the panel's
/// "Attach cargo encounter" action appears on any observed target you select.
/// </summary>
[BepInPlugin(Id, "Cargo recovery example", "1.0.0")]
    // Version floor omitted on purpose: in-repo examples track the development API, which
    // is deliberately unversioned (0.0.0); real consumers name the release they require.
    [BepInDependency(ModApi.PluginId)]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Id = "vgmodapi.example.cargo";

    private CargoAuthorSession? _session;
    private IWorldProvider? _world;
    private ITravelService? _travel;
    private ILifecycleService? _lifecycle;
    private IHudRegistration? _hud;
    private DerelictSite? _derelict;
    private float _nextAdoptionAttempt;

    // Wiring is deferred to Start() for the same reason across every example in this repository:
    // BepInEx only populates Chainloader.PluginInfos[].Instance after Awake returns, and the world
    // provider authenticates the caller against exactly that entry.
    private void Start()
    {
        var reward = Config.Bind("Content", "RewardItemId", "SalvageCarbon",
            "Existing game item identifier for shipment rewards. Blank registers no content; the default is a stock salvage item.").Value;
        try
        {
            _session = new CargoAuthorSession(reward, ModApi.Services.Lifecycle, ModApi.Services.Dungeons,
                message => Logger.LogInfo(message), message => Logger.LogWarning(message));
        }
        catch (Exception error) { Logger.LogError(error); }

        // The example authors its own boarding target, so world authoring is required, not optional.
        _world = ModApi.Services.World.AcquireProvider(this);
        if (_world == null) Logger.LogWarning("World authoring unavailable: this example cannot create its derelict.");
        else
            // Declare the world content NOW, while no session exists: world declarations are refused
            // with NotReady once a session is running. The encounter is resolved lazily because
            // dungeon content only registers at gameplay readiness.
            _derelict = new DerelictSite(_world, () => _session?.Encounter, message => Logger.LogInfo(message));
        if (_session != null) _session.OwnInstallation = () => _derelict?.Installation;
        _travel = ModApi.Services.Travel;
        // POI handles belong to one session: drop them when it ends and re-obtain after a load,
        // or the HUD reports a derelict the replacement save never had.
        _lifecycle = ModApi.Services.Lifecycle;
        _lifecycle.Changed += OnLifecycle;
        _hud = ModApi.Services.Hud.Register(Id, "panel", OnHud);
        RefreshPanel();
    }

    /// <summary>
    /// Adoption cannot rely on boarding observations alone. Attaching is a mutation, and the API blocks
    /// mutations while its own save/settlement/reward/rule work is in flight, so an attach attempted at
    /// the moment an event arrives can be refused as transiently Unavailable. Retry on a cheap tick
    /// until it takes, instead of treating one refusal as final.
    /// </summary>
    private void Update()
    {
        if (_session?.Encounter == null || _session.Attached || Time.unscaledTime < _nextAdoptionAttempt) return;
        _nextAdoptionAttempt = Time.unscaledTime + 1f;
        if (_derelict?.Installation == null) return;
        _session.TryAdoptOwnStation();
        if (_session.Attached) RefreshPanel();
    }

    private void OnLifecycle(LifecycleEvent message)
    {
        if (message.Kind == LifecycleEventKind.SessionInvalidated) { _derelict?.ForgetSession(); RefreshPanel(); }
        else if (message.Kind == LifecycleEventKind.GameplayInitialized) { _derelict?.Reacquire(); RefreshPanel(); }
    }

    private void RefreshPanel()
    {
        if (_hud == null) return;
        var derelict = _derelict;
        var ready = _world != null && _session?.Encounter != null;
        var spawned = derelict?.Exists == true;

        _hud.Update(null, new HudPanel("Cargo Recovery",
            new[]
            {
                new HudRow("spawn", spawned ? "Derelict spawned" : "Spawn derelict",
                    "create an owned derelict station in this system",
                    "Creates an owned salvage site here, declared withStation, which guarantees a native derelict "
                    + "station. The station is held enterable so ambient damage cannot ruin it before you reach it.",
                    clickable: ready && !spawned),
                new HudRow("attach", _session?.Attached == true ? "Layout attached" : "Layout attaches when observed",
                    "the derelict adopts this mod's compartment layout by itself",
                    "Attaches by installation identity as soon as a live boarding target belongs to the authored station. "
                    + "Until then the API answers StaleTarget, a temporary refusal that is simply retried. No vanilla "
                    + "encounter is ever touched.",
                    clickable: false),
                new HudRow("remove", "Remove derelict",
                    "remove the station, its site and the cargo attachment",
                    "Releases the enterable hold, asks CanRemove(), then removes the site. If you are at it, boarding "
                    + "it, or it has a persisted interior, removal is queued for the next safe cleanup window "
                    + "instead - Remove() itself does not check player safety.",
                    clickable: spawned),
                new HudRow("status", StatusLine(),
                    "Board the station, walk airlock -> cargo hold -> control room, then take the shipment choice."),
            },
            closable: false));
    }

    private string StatusLine()
    {
        if (_session?.Encounter == null) return "content not registered - check the log for the reward item id";
        var derelict = _derelict;
        if (derelict?.Exists != true) return "ready | at " + (_travel?.CurrentLocation?.SystemName ?? _travel?.CurrentLocation?.SystemId ?? "nowhere");
        return $"site:{derelict.SiteState} attached:{_session.Attached} | poi:{derelict.StationPoiId ?? "pending"}";
    }

    private void OnHud(HudInteraction interaction)
    {
        if (interaction.Kind != HudInteractionKind.Row) return;
        try
        {
            switch (interaction.RowId)
            {
                case "spawn":
                    var system = _travel?.CurrentLocation?.SystemId;
                    if (string.IsNullOrEmpty(system)) { Logger.LogWarning("No current system to place the derelict in."); break; }
                    _derelict?.Spawn(system!);
                    break;
                case "remove":
                    if (_derelict?.Remove() == true) _session?.ResetAdoption();
                    break;
            }
            RefreshPanel();
        }
        catch (Exception error) { Logger.LogError(error); RefreshPanel(); }
    }

    private void OnDestroy()
    {
        if (_lifecycle != null) { _lifecycle.Changed -= OnLifecycle; _lifecycle = null; }
        var hud = _hud; _hud = null; hud?.Dispose();
        _derelict?.Dispose(); _derelict = null;
        _session?.Dispose(); _session = null;
        _world?.Dispose(); _world = null;
        _travel = null;
    }
}
