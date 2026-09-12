using System;
using System.Collections.Generic;
using BepInEx;
using VGModAPI;

namespace PocketWorlds;

/// <summary>
/// Sample/test mod demonstrating pocket-cluster + wormhole + themed-site + combat-site authoring
/// together, and the full-cleanup surface (each owned occurrence removes back out).
///
/// From your current system X you press "Spawn Wormhole" to open a wormhole into a small authored
/// cluster. The cluster is a chain of owned pockets:
///
///   X  --wormhole-->  E (Cluster Entry)   --gate-->  A (Hub Alpha)
///                                                 \--gate-->  B (Anchor Beta)
///
///   A  --[gate back to E]--    + two wormholes into themed off-world instances:
///                                  * off-world "Mining"  (mining field site)
///                                  * off-world "Salvage" (salvage wreck site)
///   B  --[gate back to E]--    a guarded dead-end: it holds an owned COMBAT site
///
/// Every system has a fixed test name (static, not changing). "Delete Cluster" removes each owned
/// occurrence — the wormhole pair first (its endpoints must be freed), then each pocket (its own gate
/// and site POIs go with it) — so a delete leaves no authored system, gate, wormhole or site behind.
/// </summary>
[BepInPlugin(Id, "Pocket Worlds example", "1.0.0")]
[BepInDependency(ModApi.PluginId, "0.2.10")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Id = "vgmodapi.example.pocket-worlds";
    private const string DisplayName = "Pocket Worlds example";

    // Static test names (requirement 6): they never change.
    private const string EntryName = "Cluster Entry";
    private const string HubName = "Hub Alpha";
    private const string AnchorName = "Anchor Beta";
    private const string MiningWorldName = "Mining Instance";
    private const string SalvageWorldName = "Salvage Instance";
    // Static names for the two subsectors the cluster allocates (the cluster itself, and the outside
    // instance). Without these the game would generate a procedural subsector name.
    private const string ClusterSectorName = "Wormhole Cluster";
    private const string SalvageSectorName = "Salvage Drift";

    // Local definition identities (author-local; API owns native ids).
    private const string EntryDef = "cluster-entry";
    private const string HubDef = "cluster-hub";
    private const string AnchorDef = "cluster-anchor";
    private const string MiningDef = "cluster-mining";
    private const string SalvageDef = "cluster-salvage";
    private const string EntryDoorDef = "cluster-door";
    private const string MiningWormholeDef = "cluster-mining-hole";
    private const string SalvageWormholeDef = "cluster-salvage-hole";
    private const string MiningSiteDef = "cluster-mining-site";
    private const string SalvageSiteDef = "cluster-salvage-site";
    private const string GuardDef = "cluster-guard";

    private IWorldProvider? _world;
    private ITravelService? _travel;
    private ILifecycleService? _lifecycle;
    private IHudRegistration? _hud;

    private IPocketSystem? _entry;   // E
    private IPocketSystem? _hub;     // A
    private IPocketSystem? _anchor;  // B
    private IPocketSystem? _mining;  // off-world: mining-only
    private IPocketSystem? _salvage; // off-world: salvage-only
    private IWormholePair? _entryDoor;   // X <-> E
    private IWormholePair? _miningHole;  // A <-> mining
    private IWormholePair? _salvageHole; // A <-> salvage
    private IResourceSite? _miningSite;
    private IResourceSite? _salvageSite;
    private ICombatSite? _guard;   // owned combat site inside Anchor Beta

    private void Awake()
    {
        // Registration is deferred to Start(): BepInEx only populates PluginInfos[].Instance after
        // Awake returns, so host authentication (matching the plugin instance) cannot succeed here.
    }

    private void Start()
    {
        _world = ModApi.Services.World.AcquireProvider(this);
        if (_world == null) { Logger.LogWarning(DisplayName + ": world authoring unavailable."); return; }

        // Register immutable definitions (pre-session; registering never creates native objects).
        // The cluster lives in its OWN subsector, built by combining placements:
        //   * Entry (E) is OffMap  -> it creates a fresh, remote subsector: the cluster.
        //   * Hub (A), Anchor (B) and Mining are Visible anchored to a cluster system, and Visible
        //     places a pocket in its ANCHOR's own subsector -> they land inside E's subsector, not yours.
        //   * Salvage is OffMap -> its own separate subsector outside the cluster.
        // The result is one cluster subsector holding Entry + Hub + Anchor + Mining, plus one
        // outside instance, with every system named statically.
        // quiet: true on every cluster system — the cluster is a private place, so nothing vanilla
        // spawns inside it (no station visitors, no passerby traffic at its gates, no security patrols).
        _world.RegisterPocketSystem(new PocketSystemDefinition(EntryDef, 1, EntryName, PocketSystemPlacement.OwnSector, factionId: null, sectorName: ClusterSectorName, quiet: true));
        _world.RegisterPocketSystem(new PocketSystemDefinition(HubDef, 1, HubName, PocketSystemPlacement.Visible, factionId: null, sectorName: null, quiet: true));
        _world.RegisterPocketSystem(new PocketSystemDefinition(AnchorDef, 1, AnchorName, PocketSystemPlacement.Visible, factionId: null, sectorName: null, quiet: true));
        _world.RegisterPocketSystem(new PocketSystemDefinition(MiningDef, 1, MiningWorldName, PocketSystemPlacement.Visible, factionId: null, sectorName: null, quiet: true));
        _world.RegisterPocketSystem(new PocketSystemDefinition(SalvageDef, 1, SalvageWorldName, PocketSystemPlacement.OffMap, factionId: null, sectorName: SalvageSectorName, quiet: true));
        // quiet: true — these are owned passages, not highways: no passerby ships fly through them and
        // no security patrol is created at either end.
        _world.RegisterWormholePair(new WormholePairDefinition(EntryDoorDef, 1, "Cluster Rift", quiet: true));
        _world.RegisterWormholePair(new WormholePairDefinition(MiningWormholeDef, 1, MiningWorldName + " Rift", quiet: true));
        _world.RegisterWormholePair(new WormholePairDefinition(SalvageWormholeDef, 1, SalvageWorldName + " Rift", quiet: true));
        _world.RegisterResourceSite(ResourceSiteDefinition.MiningField(MiningSiteDef, 1, MiningWorldName + " Field", 12, 8));
        _world.RegisterResourceSite(ResourceSiteDefinition.Salvage(
            SalvageSiteDef, 1, SalvageWorldName + " Wreck", 8, wreckShipId: "Monsoon", factionId: "Fanatics",
            withStation: false, hazard: null, scatterAsteroids: false));
        // An owned persistent COMBAT site. Like a resource site it lives inside a pocket, so it is
        // removed with that pocket. A combat site also has its own Remove(), but letting the pocket
        // take it is simpler and is what this example demonstrates.
        _world.RegisterCombatSite(new CombatSiteDefinition(GuardDef, 1, AnchorName + " Guard", "Fanatics", 1));

        _travel = ModApi.Services.Travel;
        // Occurrence objects belong to ONE session: an occurrence from an ended session keeps its last
        // observed state and never resolves against the replacement save. Drop the handles when a
        // session ends and re-obtain them after a load, or this panel reports a cluster that the
        // loaded save does not contain.
        _lifecycle = ModApi.Services.Lifecycle;
        _lifecycle.Changed += OnLifecycle;
        _hud = ModApi.Services.Hud.Register(Id, "panel", OnHud);
        RefreshPanel();
    }

    private void OnLifecycle(LifecycleEvent message)
    {
        if (message.Kind == LifecycleEventKind.SessionInvalidated) ForgetSession();
        else if (message.Kind == LifecycleEventKind.GameplayInitialized) Reacquire();
        else return;
        RefreshPanel();
    }

    /// <summary>Drops every handle from a session that has ended; it can never resolve again.</summary>
    private void ForgetSession()
    {
        _entry = _hub = _anchor = _mining = _salvage = null;
        _entryDoor = _miningHole = _salvageHole = null;
        _miningSite = _salvageSite = null; _guard = null;
    }

    /// <summary>
    /// Re-obtains the cluster for the live game after a load. The API restored the occurrences from
    /// save data; these calls only re-acquire handles to them and create nothing. Anything the save
    /// does not contain simply stays null.
    /// </summary>
    private void Reacquire()
    {
        if (_world == null) return;
        _entry = _world.GetPocketSystem(EntryDef, "cluster");
        _hub = _world.GetPocketSystem(HubDef, "hub");
        _anchor = _world.GetPocketSystem(AnchorDef, "anchor");
        _mining = _world.GetPocketSystem(MiningDef, "mining");
        _salvage = _world.GetPocketSystem(SalvageDef, "salvage");
        _entryDoor = _world.GetWormholePair(EntryDoorDef, "cluster-door");
        _miningHole = _world.GetWormholePair(MiningWormholeDef, "mining-hole");
        _salvageHole = _world.GetWormholePair(SalvageWormholeDef, "salvage-hole");
        _miningSite = _world.GetResourceSite(MiningSiteDef, "mining-site");
        _salvageSite = _world.GetResourceSite(SalvageSiteDef, "salvage-site");
        _guard = _world.GetCombatSite(GuardDef, "anchor-guard");
    }

    private void RefreshPanel()
    {
        if (_hud == null) return;
        string current = _travel?.CurrentLocation?.SystemName is { Length: > 0 } sys ? sys
            : _travel?.CurrentLocation?.SystemId ?? "nowhere";

        _hud.Update(null, new HudPanel("Pocket Worlds",
            new[]
            {
                new HudRow("location", "You are at " + current + ".", "The cluster is a wormhole-only place reached from here."),
                new HudRow("spawn", _entryDoor != null ? "Cluster ready" : "Spawn Wormhole",
                    "open a wormhole into a 5-system authored cluster",
                    "Creates Cluster Entry (E) anchored to this system, then Hub Alpha (A) and Anchor Beta (B) "
                    + "linked to E by gates. A holds two wormholes into themed off-world instances (mining, salvage), "
                    + "and B holds an owned combat site. Everything you spawn is removable later.",
                    clickable: _entryDoor == null),
                new HudRow("delete", _entryDoor == null ? "spawn first" : "Delete Cluster",
                    "remove the entry wormhole, then every authored system, gate, wormhole and site",
                    "Full cleanup: removes the wormhole pairs first (their endpoints must be freed), then each "
                    + "pocket (its gate and any site POIs go with it). Moving into any part of the cluster first "
                    + "would refuse deletion until you leave.",
                    clickable: _entryDoor != null),
                new HudRow("log", _entryDoor == null ? "spawn first" : "Log topology",
                    "write what each spawned system contains and how it is connected, to the log file",
                    "Prints one line per spawned system: the gates and wormholes it holds (with their far "
                    + "ends) and the sites inside it, so the log shows exactly how the cluster is wired.",
                    clickable: _entryDoor != null),
                new HudRow("status", StatusLine(), "Each owned occurrence shows its live reconstruction state."),
            },
            closable: false));
    }

    private string StatusLine()
    {
        string Pb(IPocketSystem? p) => p == null ? "-" : (p.State.Reconstructed ? "up" : (p.State.Status.ToString().ToLowerInvariant()));
        string Wb(IWormholePair? w) => w == null ? "-" : (w.State.Reconstructed ? "up" : "down");
        string Cb(ICombatSite? c) => c == null ? "-" : (c.State.Reconstructed ? "up" : c.State.Status.ToString().ToLowerInvariant());
        return $"E:{Pb(_entry)} A:{Pb(_hub)} B:{Pb(_anchor)} M:{Pb(_mining)} S:{Pb(_salvage)} | door:{Wb(_entryDoor)} m:{Wb(_miningHole)} s:{Wb(_salvageHole)} | guard:{Cb(_guard)}";
    }

    private void OnHud(HudInteraction interaction)
    {
        if (interaction.Kind != HudInteractionKind.Row) return;
        try
        {
            switch (interaction.RowId)
            {
                case "spawn": SpawnCluster(); break;
                case "delete": DeleteCluster(); break;
                case "log": LogTopology(); break;
            }
            RefreshPanel();
        }
        catch (Exception error) { Logger.LogError(error); RefreshPanel(); }
    }

    private bool CurrentSystem(out string systemId)
    {
        systemId = _travel?.CurrentLocation?.SystemId ?? "";
        if (systemId.Length == 0) { Logger.LogWarning("No current system to anchor the cluster to."); return false; }
        return true;
    }

    /// <summary>Creates the whole cluster from System X: entry wormhole + 3 gate-linked systems + 2
    /// wormholes into themed off-world instances, with sites in each off-world.</summary>
    private void SpawnCluster()
    {
        if (_world == null || _entryDoor != null) return;
        if (!CurrentSystem(out var x)) return;

        // E: the cluster entry, a pocket anchored to X. Its own gate to X stays sealed; the wormhole is the door.
        _entry = _world.CreatePocketSystem(EntryDef, "cluster", x);
        if (_entry?.SystemId == null) { Logger.LogWarning("Failed to create the cluster entry pocket."); _entry = null; return; }

        // The entry wormhole X <-> E (the "first wormhole").
        _entryDoor = _world.CreateWormholePair(EntryDoorDef, "cluster-door", x, _entry.SystemId);
        if (_entryDoor == null)
        { Logger.LogWarning("Failed to link the entry wormhole; removing the bare entry."); _entry.Remove(); _entry = null; return; }
        _entryDoor.SetOpen(true);

        // A: Hub Alpha, anchored to E (gate E <-> A). Open it so the gate is a real way to travel.
        _hub = _world.CreatePocketSystem(HubDef, "hub", _entry.SystemId);
        if (_hub?.SystemId == null) { Logger.LogWarning("Failed to create Hub Alpha."); _hub = null; return; }
        _hub.SetEntranceOpen(true);

        // B: Anchor Beta, anchored to E (gate E <-> B). Quiet dead-end with just the gate back.
        _anchor = _world.CreatePocketSystem(AnchorDef, "anchor", _entry.SystemId);
        if (_anchor?.SystemId == null) { Logger.LogWarning("Failed to create Anchor Beta."); _anchor = null; return; }
        _anchor.SetEntranceOpen(true);
        // The dead-end is guarded: an owned combat site keyed by an author-local occurrence key. The
        // API allocates the native identity; GetCombatSite re-obtains this same object after a reload.
        _guard = _world.CreateCombatSite(GuardDef, "anchor-guard", _anchor.SystemId, 0f, 0f);

        // A has two wormholes into themed off-world instances (mining-only, salvage-only).
        var mining = _world.CreatePocketSystem(MiningDef, "mining", _hub.SystemId);
        if (mining?.SystemId != null)
        {
            _mining = mining;
            _miningHole = _world.CreateWormholePair(MiningWormholeDef, "mining-hole", _hub.SystemId, _mining.SystemId);
            _miningHole?.SetOpen(true);
            _miningSite = _world.CreateResourceSite(MiningSiteDef, "mining-site", _mining.SystemId, 0f, 0f);
        }
        var salvage = _world.CreatePocketSystem(SalvageDef, "salvage", _hub.SystemId);
        if (salvage?.SystemId != null)
        {
            _salvage = salvage;
            _salvageHole = _world.CreateWormholePair(SalvageWormholeDef, "salvage-hole", _hub.SystemId, _salvage.SystemId);
            _salvageHole?.SetOpen(true);
            _salvageSite = _world.CreateResourceSite(SalvageSiteDef, "salvage-site", _salvage.SystemId, 0f, 0f);
        }

        Logger.LogInfo("Pocket Worlds ready: fly the rift from " + (NameOf(EntryDef)) + " through Hub Alpha into either off-world.");
        LogTopology();
    }

    /// <summary>
    /// Writes what was actually authored: one line per spawned system listing the gates and wormholes it
    /// holds (with the far end each one leads to) and any site inside it. This is the ground truth a player
    /// can compare against the in-game map when a connection looks surprising.
    /// </summary>
    private void LogTopology()
    {
        if (_world == null || _entryDoor == null) return;
        Logger.LogInfo("=== Pocket Worlds topology ===");
        Logger.LogInfo($"subsector: {ClusterSectorName} (contains {EntryName}, {HubName}, {AnchorName}, {MiningWorldName})");
        Logger.LogInfo($"subsector: {SalvageSectorName} (contains {SalvageWorldName}) - off-map: exists and works, but the galaxy map cannot pan or zoom to it");
        Logger.LogInfo($"origin: {OriginSystemName()} --wormhole[{_entryDoor.Definition.Name}]--> {EntryName}");
        Logger.LogInfo("note: each authored system also holds one SEALED, HIDDEN anchor gate back to the system it "
            + "was anchored to; it is not a usable connection and is deliberately not drawn on the map, so it is "
            + "not listed below.");

        Describe(EntryName, _entry, new[]
        {
            Gate("to " + HubName, _hub),
            Gate("to " + AnchorName, _anchor),
        });
        Describe(HubName, _hub, new[]
        {
            Gate("back to " + EntryName, _entry),
            Wormhole("to " + MiningWorldName, _miningHole),
            Wormhole("to " + SalvageWorldName, _salvageHole),
        });
        Describe(AnchorName, _anchor, new[ ] { Gate("back to " + EntryName, _entry) });
        Describe(MiningWorldName, _mining, new[] { Wormhole("to " + HubName, _miningHole) });
        Describe(SalvageWorldName, _salvage, new[] { Wormhole("to " + HubName, _salvageHole) });
        Logger.LogInfo("=== end topology ===");
    }

    private static string Gate(string to, IPocketSystem? peer)
        => peer == null ? "gate " + to + " (not spawned)" : "gate " + to;
    private static string Wormhole(string to, IWormholePair? pair)
        => pair == null ? "wormhole " + to + " (not spawned)" : "wormhole " + to;

    private void Describe(string name, IPocketSystem? system, string[] connections)
    {
        if (system == null) { Logger.LogInfo(name + ": not spawned"); return; }
        string sites = name == MiningWorldName && _miningSite != null ? " | site: mining field"
            : name == SalvageWorldName && _salvageSite != null ? " | site: salvage wreck"
            : name == AnchorName && _guard != null ? " | site: combat guard (state=" + _guard.State.Status + ")"
            : "";
        Logger.LogInfo($"{name}: {connections.Length} connection(s) -> {string.Join(", ", connections)} | system={system.SystemId} state={system.State.Status}{sites}");
    }

    private string OriginSystemName()
        => _travel?.CurrentLocation?.SystemName is { Length: > 0 } n ? n : (_travel?.CurrentLocation?.SystemId ?? "?");

    private string NameOf(string def) => def switch
    {
        EntryDef => EntryName, HubDef => HubName, AnchorDef => AnchorName,
        MiningDef => MiningWorldName, SalvageDef => SalvageWorldName, _ => def
    };

    /// <summary>Full cleanup (requirement 8): remove the wormhole pairs first (a pocket that is still a
    /// wormhole endpoint cannot be removed), then each pocket — its gate and site POIs go with it.</summary>
    private void DeleteCluster()
    {
        if (_world == null || _entryDoor == null) return;

        // Order is dictated by the API's integrity rules, not by taste:
        //   * a pocket that is still a wormhole endpoint cannot be removed, so pairs go first;
        //   * a pocket that still contains an owned COMBAT site cannot be removed either, so the guard
        //     goes before its anchor. (Resource sites are different: they DO go with their pocket.)
        if (!RemoveWormhole("mining rift", _miningHole)) return;   _miningHole = null;
        if (!RemoveWormhole("salvage rift", _salvageHole)) return; _salvageHole = null;
        if (!RemoveWormhole("entry rift", _entryDoor)) return;     _entryDoor = null;

        // Off-world pockets (their resource-site rows drop with the pocket).
        if (!RemovePocket(MiningWorldName, _mining)) return;   _mining = null; _miningSite = null;
        if (!RemovePocket(SalvageWorldName, _salvage)) return; _salvage = null; _salvageSite = null;
        if (!RemovePocket(HubName, _hub)) return; _hub = null;
        if (!RemoveCombatSite(AnchorName + " guard", _guard)) return; _guard = null;
        if (!RemovePocket(AnchorName, _anchor)) return; _anchor = null;
        if (!RemovePocket(EntryName, _entry)) return; _entry = null;

        Logger.LogInfo("Pocket Worlds deleted: every authored system, gate, wormhole and site is gone.");
    }

    private bool RemoveWormhole(string label, IWormholePair? w)
        => w == null || Teardown(label, w.CanRemove(), w.Remove, w.RequestRemoval);

    private bool RemovePocket(string label, IPocketSystem? p)
        => p == null || Teardown(label, p.CanRemove(), p.Remove, p.RequestRemoval);

    private bool RemoveCombatSite(string label, ICombatSite? c)
        => c == null || Teardown(label, c.CanRemove(), c.Remove, c.RequestRemoval);

    /// <summary>
    /// Removes one occurrence safely, and returns true only when it actually went away.
    ///
    /// `Remove()` is the PLAIN native removal: it refuses only when removal would be impossible or
    /// would corrupt save state, and deliberately does NOT check transient player-safety conditions.
    /// So a consumer that does not want to tear the world out from under the player must ask
    /// `CanRemove()` first. When the answer is not Ready, this hands the job to `RequestRemoval()`,
    /// which mirrors the game's own ambient cleanup window and completes once the condition clears.
    ///
    /// Returning false stops the cascade, so a half-torn cluster is never reported as cleared.
    /// </summary>
    private bool Teardown(string label, RemovalStatus status, Func<WorldContentResult> remove, Func<WorldContentResult> request)
    {
        if (status == RemovalStatus.NotPresent) return true; // nothing there to remove
        if (status == RemovalStatus.Ready)
        {
            var result = remove();
            if (result.Succeeded) return true;
            Logger.LogWarning($"{label} removal refused: {result.Status} - {result.Detail}");
            return false;
        }
        var deferred = request();
        Logger.LogInfo($"{label} cannot be removed yet ({status}); "
            + (deferred.Succeeded ? "queued for the next safe cleanup window." : "deferral refused: " + deferred.Status + " - " + deferred.Detail));
        return false;
    }

    private void OnDestroy()
    {
        if (_lifecycle != null) { _lifecycle.Changed -= OnLifecycle; _lifecycle = null; }
        if (_world != null) _world.Dispose();
        var hud = _hud; _hud = null; hud?.Dispose();
        _travel = null; _world = null;
    }
}
