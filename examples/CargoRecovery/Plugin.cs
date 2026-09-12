using System;
using BepInEx;
using VGModAPI;
namespace CargoRecovery;

/// <summary>
/// Sample/test mod demonstrating authored boarding content end to end. It supplies its own target:
/// one HUD button spawns an owned derelict station, the cargo layout attaches to it, and a second
/// button removes the whole thing again.
///
/// It still works as a general contextual control for vanilla encounters — the panel's
/// "Attach cargo encounter" action appears on any observed target you select.
/// </summary>
[BepInPlugin(Id, "Cargo recovery example", "1.0.0")]
[BepInDependency(ModApi.PluginId, "0.2.10")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Id = "vgmodapi.example.cargo";

    private CargoAuthorSession? _session;
    private IWorldProvider? _world;
    private ITravelService? _travel;
    private IHudRegistration? _hud;
    private DerelictSite? _derelict;

    // Wiring is deferred to Start() for the same reason across every example in this repository:
    // BepInEx only populates Chainloader.PluginInfos[].Instance after Awake returns, and the world
    // provider authenticates the caller against exactly that entry.
    private void Start()
    {
        var reward = Config.Bind("Content", "RewardItemId", "SalvageCarbon",
            "Existing game item identifier for shipment rewards. Blank registers no content; the default is a stock salvage item.").Value;
        try
        {
            _session = new CargoAuthorSession(reward, ModApi.Services.Lifecycle, ModApi.Services.DungeonOperations, ModApi.Services.Dungeons,
                ModApi.Services.DungeonPanel, ModApi.Services.DungeonCommands, ModApi.Services.DungeonTactics, ModApi.Services.DungeonSettlement,
                message => Logger.LogInfo(message), message => Logger.LogWarning(message));
        }
        catch (Exception error) { Logger.LogError(error); }

        // The example authors its own boarding target, so world authoring is required, not optional.
        _world = ModApi.Services.World.AcquireProvider(this);
        if (_world == null) Logger.LogWarning("World authoring unavailable: this example cannot create its derelict.");
        if (_session != null) _session.OwnInstallation = () => _derelict?.Installation;
        _travel = ModApi.Services.Travel;
        _hud = ModApi.Services.Hud.Register(Id, "panel", OnHud);
        RefreshPanel();
    }

    /// <summary>Created on first use: the encounter must be registered before a target can adopt it.</summary>
    private DerelictSite? Derelict()
    {
        if (_derelict != null) return _derelict;
        if (_world == null || _session?.Encounter == null) return null;
        _derelict = new DerelictSite(_world, _session.Encounter, message => Logger.LogInfo(message));
        return _derelict;
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
                    "create an owned derelict station to board",
                    "Creates a quiet pocket system beside your current one with its gate open, and an owned salvage "
                    + "site inside it declared withStation, which guarantees a native derelict station. The station is "
                    + "held enterable so ambient damage cannot ruin it before you arrive.",
                    clickable: ready && !spawned),
                new HudRow("attach", _session?.Attached == true ? "Layout attached" : "Layout attaches on arrival",
                    "the derelict adopts this mod's compartment layout by itself",
                    "Attaches by installation identity as soon as a live boarding target belongs to the authored station, "
                    + "which happens when you arrive. Until then the API answers StaleTarget — a temporary refusal that is "
                    + "simply retried. No vanilla encounter is ever touched.",
                    clickable: false),
                new HudRow("remove", "Remove derelict",
                    "dissolve the station, the site and the system together",
                    "Authored sites have no Dissolve of their own: they are removed with the pocket that holds them. "
                    + "A dissolve is refused while you are inside \u2014 leave first.",
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
        return $"system:{derelict.PocketState} site:{derelict.SiteState} attached:{_session.Attached} | poi:{derelict.StationPoiId ?? "pending"}";
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
                    if (string.IsNullOrEmpty(system)) { Logger.LogWarning("No current system to anchor the derelict to."); break; }
                    Derelict()?.Spawn(system!);
                    break;
                case "remove":
                    if (Derelict()?.Remove() == true) _session?.ResetAdoption();
                    break;
            }
            RefreshPanel();
        }
        catch (Exception error) { Logger.LogError(error); RefreshPanel(); }
    }

    private void OnDestroy()
    {
        var hud = _hud; _hud = null; hud?.Dispose();
        _derelict?.Dispose(); _derelict = null;
        _session?.Dispose(); _session = null;
        _world?.Dispose(); _world = null;
        _travel = null;
    }
}
