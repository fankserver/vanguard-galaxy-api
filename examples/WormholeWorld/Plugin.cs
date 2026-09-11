using System;
using BepInEx;
using VGModAPI;

namespace WormholeWorld;

/// <summary>
/// Sample/test mod demonstrating the authored pocket-system + wormhole-pair surfaces together.
///
/// Press "Spawn Wormhole World" while flying: it creates an authored pocket system anchored
/// next to your current system, then links them with a native wormhole pair. On your end of the
/// pair (the door you spawn near) you fly into the wormhole and arrive in the pocket "world";
/// the pocket-side wormhole is the very same door, so using it carries you back to the system
/// you came from. The pair is exactly connected to itself, so it never leaks into the global
/// wormhole mesh and needs no wormhole-unlock progression.
/// </summary>
[BepInPlugin(Id, "Wormhole World example", "1.0.0")]
[BepInDependency(ModApi.PluginId, "0.2.10")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Id = "vgmodapi.example.wormhole-world";
    private const string DisplayName = "Wormhole World example";
    private const string PocketDef = "wormhole-pocket";
    private const string VisibleDef = "visible-pocket";
    private const string DoorDef = "wormhole-door";
    private const string FieldDef = "wormhole-field";

    private IWorldProvider? _world;
    private ITravelService? _travel;
    private IHudRegistration? _hud;

    // The owned Pocket-side door and the authored pair for the current game.
    private IPocketSystem? _pocket;
    private IPocketSystem? _visiblePocket;
    private IWormholePair? _pair;
    private IResourceSite? _field;
    private bool _open = true;

    private void Awake()
    {
        // Definitions are registered before any game session starts; registering never creates native
        // objects. Registration is deferred to Start(): BepInEx only populates PluginInfos[].Instance
        // AFTER the plugin's Awake returns, so host authentication (which matches the plugin instance in
        // Chainloader.PluginInfos) cannot succeed in Awake. Start() runs after BepInEx finishes loading
        // and still before the first session, which is exactly when providers may be acquired.
    }

    private void Start()
    {
        // Register immutable definitions safely (pre-session, authenticated host); creation happens
        // later, on explicit gameplay input. Registering never creates native objects.
        _world = ModApi.Services.World.AcquireProvider(this);
        if (_world == null)
        {
            Logger.LogWarning(DisplayName + ": world authoring unavailable; the door will not spawn.");
            return;
        }
        _world.RegisterPocketSystem(new PocketSystemDefinition(PocketDef, 1, "Wormhole World"));
        _world.RegisterPocketSystem(new PocketSystemDefinition(VisibleDef, 1, "Visible Pocket", PocketSystemPlacement.Visible));
        _world.RegisterWormholePair(new WormholePairDefinition(DoorDef, 1, "Unstable Rift"));
        _world.RegisterResourceSite(ResourceSiteDefinition.MiningField(FieldDef, 1, "Rift Field", 12, 6));

        _travel = ModApi.Services.Travel;
        _hud = ModApi.Services.Hud.Register(Id, "panel", OnHud);
        RefreshPanel();
    }

    /// <summary>Re-renders the button panel, showing only what is actionable right now.</summary>
    private void RefreshPanel()
    {
        if (_hud == null || _travel == null) return;
        string current = _travel.CurrentLocation?.SystemName is { Length: > 0 } sys ? sys
            : _travel.CurrentLocation?.SystemId ?? "nowhere";
        string status = _pair?.State.Reconstructed == true
            ? "door: " + (_open ? "open" : "closed")
            : "no door yet";

        _hud.Update(null, new HudPanel("Wormhole World",
            new[]
            {
                new HudRow("location", "You are at " + current + ".", "Known systems stay reachable; the pocket world is a separate place."),
                new HudRow("spawn", "Spawn Wormhole World (OffMap)",
                    "create an ISOLATED pocket world + the paired door",
                    "Creates a separate pocket in a remote empty sector (off the settled map, wormhole-only). "
                    + "The map around it is intentionally blank. Fly the wormhole to enter it.",
                    clickable: _pair == null),
                new HudRow("spawnVisible", _visiblePocket != null ? "Visible pocket exists" : "Spawn Visible Pocket",
                    "a pocket in THIS sector, on the settled map",
                    "Creates a separate pocket in your current system's own sector, rendered as its own dot "
                    + "on the settled belt map. To see it: stay in this sector and open the belt map "
                    + "(do NOT fly the OffMap wormhole).",
                    clickable: _visiblePocket == null),
                new HudRow("toggle", _pair == null ? "spawn first" : (_open ? "Close Door" : "Open Door"),
                    "Open shows/enables both ends; closed hides them (a way to seal the world).",
                    "The door stays exactly connected to itself, so closing it seals the pocket.",
                    clickable: _pair != null),
                new HudRow("field", _field != null ? "field exists" : "Add a mining field in the pocket",
                    "A little content so the pocket world is worth visiting.",
                    "Places a mining field inside the pocket world.",
                    clickable: _pair != null && _field == null),
                new HudRow("status", status, "The pocket-side wormhole is the same door back to your origin."),
            },
            closable: false));
    }

    private void OnHud(HudInteraction interaction)
    {
        if (interaction.Kind != HudInteractionKind.Row) return;
        try
        {
            switch (interaction.RowId)
            {
                case "spawn": SpawnWorld(); break;
                case "spawnVisible": SpawnVisiblePocket(); break;
                case "toggle": ToggleDoor(); break;
                case "field": SpawnField(); break;
            }
            RefreshPanel();
        }
        catch (Exception error) { Logger.LogError(error); RefreshPanel(); }
    }

    /// <summary>Creates a Visible-placement pocket: an adjacent system in the anchor's own sector that
    /// renders on the settled belt/galaxy map. Unlike the OffMap world, it is not hidden away off-map.</summary>
    private void SpawnVisiblePocket()
    {
        if (_world == null || _travel == null || _visiblePocket != null) return;
        var here = _travel.CurrentLocation;
        if (here == null) { Logger.LogWarning("No current system to anchor the visible pocket to."); return; }
        _visiblePocket = _world.CreatePocketSystem(VisibleDef, "visible-world", here.SystemId);
        if (_visiblePocket?.SystemId == null)
        {
            Logger.LogWarning("Failed to create the visible pocket.");
            _visiblePocket = null; return;
        }
        // Keep the pocket's own jump-gate entrance sealed; it is a self-contained place, not a hub.
        _visiblePocket.SetEntranceOpen(false);
        Logger.LogInfo($"Visible pocket created: system id {_visiblePocket.SystemId} in the sector of anchor system \"{here.SystemName}\" ({here.SystemId}). "
            + "It renders as a separate dot on the settled belt/galaxy map. NOTE: to see it, stay in THIS "
            + "anchor sector and open the belt map — do NOT fly into the OffMap wormhole world.");
    }

    /// <summary>Creates the pocket world and the wormhole pair linking your current system to it.</summary>
    private void SpawnWorld()
    {
        if (_world == null || _travel == null) return;
        var here = _travel.CurrentLocation;
        if (here == null) { Logger.LogWarning("No current system to anchor the pocket to."); return; }
        if (_pair != null) return;

        // The pocket is created empty (no storyteller): nothing is generated inside unless we add it.
        _pocket = _world.CreatePocketSystem(PocketDef, "current-world", here.SystemId);
        if (_pocket?.SystemId == null)
        {
            Logger.LogWarning("Failed to create the pocket system; rejecting a half-open door.");
            _pocket = null; return;
        }
        // The pocket's own jump-gate entrance is irrelevant: the wormhole pair is the door, so keep
        // the pocket sealed to any other traffic.
        _pocket.SetEntranceOpen(false);

        // Link the two systems with a native wormhole pair. The door on the pocket side is the same
        // door used to leave — the pair is exactly connected to itself.
        _pair = _world.CreateWormholePair(DoorDef, "current-door", here.SystemId, _pocket.SystemId);
        if (_pair == null)
        {
            Logger.LogWarning("Failed to link the wormhole pair; dissolving the bare pocket.");
            _pocket.Dissolve(); _pocket = null; return;
        }
        _open = true;
        _pair.SetOpen(true);
        Logger.LogInfo("Wormhole world ready: fly into the door near your location to cross the rift.");
    }

    private void ToggleDoor()
    {
        if (_pair == null) return;
        _open = !_open;
        var result = _pair.SetOpen(_open);
        if (!result.Succeeded) { _open = !_open; Logger.LogWarning("Could not " + (_open ? "open" : "close") + " the door: " + result.Detail); }
    }

    /// <summary>Adds a mining field inside the pocket world so it has a little content.</summary>
    private void SpawnField()
    {
        if (_world == null || _pocket?.SystemId == null || _field != null) return;
        _field = _world.CreateResourceSite(FieldDef, "rift-field", _pocket.SystemId, 0f, 0f);
    }

    private void OnDestroy()
    {
        if (_world != null) _world.Dispose();
        var hud = _hud; _hud = null; hud?.Dispose();
        _travel = null; _world = null;
    }
}
