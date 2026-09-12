using System;
using System.Collections.Generic;
using BepInEx;
using VGModAPI;

namespace PocketWorlds;

/// <summary>
/// Sample/test mod demonstrating pocket-cluster + wormhole + themed-site + combat-site authoring
/// together, and the full-cleanup surface (each owned occurrence dissolves back out).
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
/// Every system has a fixed test name (static, not changing). "Delete Cluster" dissolves each owned
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
        // removed with that pocket; a combat site has no Dissolve of its own.
        _world.RegisterCombatSite(new CombatSiteDefinition(GuardDef, 1, AnchorName + " Guard", "Fanatics", 1));

        _travel = ModApi.Services.Travel;
        _hud = ModApi.Services.Hud.Register(Id, "panel", OnHud);
        RefreshPanel();
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
                    + "and B holds an owned combat site. Everything you spawn is dissolvable later.",
                    clickable: _entryDoor == null),
                new HudRow("delete", _entryDoor == null ? "spawn first" : "Delete Cluster",
                    "dissolve the entry wormhole, then every authored system, gate, wormhole and site",
                    "Full cleanup: dissolves the wormhole pairs first (their endpoints must be freed), then each "
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
        { Logger.LogWarning("Failed to link the entry wormhole; dissolving the bare entry."); _entry.Dissolve(); _entry = null; return; }
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

    /// <summary>Full cleanup (requirement 8): dissolve the wormhole pairs first (a pocket that is still a
    /// wormhole endpoint cannot dissolve), then each pocket — its gate and site POIs go with it.</summary>
    private void DeleteCluster()
    {
        if (_world == null || _entryDoor == null) return;

        // Wormholes first: freeing the pair releases all pocket endpoints that used them. A refused
        // dissolve (e.g. the player is at a wormhole) leaves the occurrence in place, so keep the handle
        // and reflect the real state instead of claiming it is gone.
        if (!DissolveWormhole(_miningHole)) return;   _miningHole = null;
        if (!DissolveWormhole(_salvageHole)) return;  _salvageHole = null;
        if (!DissolveWormhole(_entryDoor)) return;    _entryDoor = null;

        // Off-world pockets (their site rows drop with the pocket).
        DissolvePocket(_mining); _mining = null; _miningSite = null;
        DissolvePocket(_salvage); _salvage = null; _salvageSite = null;
        // The branch systems, then the entry.
        DissolvePocket(_hub); _hub = null;
        // The combat site has no Dissolve of its own: it is removed with the pocket that holds it.
        DissolvePocket(_anchor); _anchor = null; _guard = null;
        DissolvePocket(_entry); _entry = null;

        Logger.LogInfo("Pocket Worlds deleted: every authored system, gate, wormhole and site is gone.");
    }

    /// <summary>Dissolves a wormhole pair; returns true only when it actually went away. On refusal the
    /// pair is left in place (the cascade stops so a half-torn cluster is never presented as cleared).</summary>
    private bool DissolveWormhole(IWormholePair? w)
    {
        if (w == null) return true;
        var result = w.Dissolve();
        if (!result.Succeeded) Logger.LogWarning("Wormhole dissolve: " + result.Status + " - " + result.Detail);
        return result.Succeeded;
    }

    private void DissolvePocket(IPocketSystem? p)
    {
        if (p == null) return;
        var result = p.Dissolve();
        if (!result.Succeeded) Logger.LogWarning("Pocket dissolve: " + result.Status + " - " + result.Detail);
    }

    private void OnDestroy()
    {
        if (_world != null) _world.Dispose();
        var hud = _hud; _hud = null; hud?.Dispose();
        _travel = null; _world = null;
    }
}
