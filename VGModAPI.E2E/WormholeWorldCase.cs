using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.E2E;

/// <summary>
/// Mirrors examples/WormholeWorld/Plugin.cs end-to-end through the public API: the same static
/// test names, placements, topology (X --wormhole--&gt; E --gates--&gt; A/B; A --wormholes--&gt; Mining
/// / Salvage with a site in each off-world) and the same dependency-ordered full cleanup. It drives
/// the same <see cref="IWorldProvider"/> surface the example uses, so a future game update that
/// breaks pocket / wormhole / resource-site authoring, reconstruction, the wormhole-endpoint removal
/// guard or the cleanup cascade is caught with the failing binding named. Native travel (flying the
/// player through a rift/gate) and the HUD row-click are UI/UX the harness cannot automate and are
/// intentionally out of scope; this test asserts the authored topology and removal safety instead.
/// </summary>
internal static class WormholeWorldCase
{
    internal const string Id = "wormhole-world";

    // Mirrors examples/WormholeWorld/Plugin.cs constants.
    private const string EntryName = "Cluster Entry";
    private const string HubName = "Hub Alpha";
    private const string AnchorName = "Anchor Beta";
    private const string MiningWorldName = "Mining Instance";
    private const string SalvageWorldName = "Salvage Instance";
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
    private const string ClusterSectorName = "Wormhole Cluster";
    private const string SalvageSectorName = "Salvage Drift";

    internal static IReadOnlyList<TestStep> Steps(ILifecycleService lifecycle, List<LifecycleEvent> events, object plugin)
    {
        var s = new WorldState();
        return new[]
        {
            new TestStep("main menu", "Behaviour.UI.MainMenuUI.instance", NativeSession.MenuReady),
            new TestStep("acquire world provider", "IWorldService.AcquireProvider (no session yet)", () =>
            {
                var world = ModApi.Services.World;
                if (!world.Availability.IsAvailable)
                    throw new InvalidOperationException("World service unavailable: " + world.Availability.Reason);
                s.World = world.AcquireProvider(plugin);
                if (s.World == null) throw new InvalidOperationException("AcquireProvider returned null (must run at menu, before a session).");
                return true;
            }),
            new TestStep("register definitions", "IWorldProvider.RegisterPocketSystem / RegisterWormholePair / RegisterResourceSite", () =>
            {
                var w = s.World!;
                WorldContentStatus[] chips = {
                    w.RegisterPocketSystem(new PocketSystemDefinition(EntryDef, 1, EntryName, PocketSystemPlacement.OwnSector, null, ClusterSectorName, quiet: true)),
                    w.RegisterPocketSystem(new PocketSystemDefinition(HubDef, 1, HubName, PocketSystemPlacement.Visible, null, null, quiet: true)),
                    w.RegisterPocketSystem(new PocketSystemDefinition(AnchorDef, 1, AnchorName, PocketSystemPlacement.Visible, null, null, quiet: true)),
                    w.RegisterPocketSystem(new PocketSystemDefinition(MiningDef, 1, MiningWorldName, PocketSystemPlacement.Visible, null, null, quiet: true)),
                    w.RegisterPocketSystem(new PocketSystemDefinition(SalvageDef, 1, SalvageWorldName, PocketSystemPlacement.OffMap, null, SalvageSectorName, quiet: true)),
                    w.RegisterWormholePair(new WormholePairDefinition(EntryDoorDef, 1, "Cluster Rift", quiet: true)),
                    w.RegisterWormholePair(new WormholePairDefinition(MiningWormholeDef, 1, MiningWorldName + " Rift", quiet: true)),
                    w.RegisterWormholePair(new WormholePairDefinition(SalvageWormholeDef, 1, SalvageWorldName + " Rift", quiet: true)),
                    w.RegisterResourceSite(ResourceSiteDefinition.MiningField(MiningSiteDef, 1, MiningWorldName + " Field", 12, 8)),
                    w.RegisterResourceSite(ResourceSiteDefinition.Salvage(SalvageSiteDef, 1, SalvageWorldName + " Wreck", 8, "Monsoon", "Fanatics", withStation: false, null, scatterAsteroids: false)),
                };
                var refused = chips.Select((c, i) => c != WorldContentStatus.Succeeded ? "def#" + i + "=" + c : null)
                    .Where(s2 => s2 != null).Cast<string>().ToArray();
                if (refused.Length > 0) throw new InvalidOperationException("Registration refused: " + string.Join("; ", refused));
                return true;
            }),
            new TestStep("create ephemeral player", "GamePlayer.CreateNewGamePlayer / GameManager.StartNewGame", () =>
            {
                var availability = lifecycle.SessionTracking.Availability;
                if (!availability.IsAvailable)
                    throw new InvalidOperationException("SessionTracking: " + availability.Reason + ": " + availability.Detail);
                NativeSession.Create();
                return true;
            }),
            new TestStep("initialize gameplay", "GameplayManager.Start / lifecycle SessionTracking", () =>
            {
                NativeSession.RequireEphemeral();
                return NativeSession.Initialized() && lifecycle.CurrentSession?.Phase == SessionPhase.GameplayInitialized;
            }),
            new TestStep("anchor at current system", "ITravelService.CurrentLocation", () =>
            {
                NativeSession.RequireEphemeral();
                var location = ModApi.Services.Travel.CurrentLocation;
                if (location == null || string.IsNullOrEmpty(location.SystemId)) return false; // player not placed yet
                s.Anchor = location.SystemId;
                return true;
            }),
            new TestStep("create entry pocket", "IWorldProvider.CreatePocketSystem", () =>
            {
                s.Entry ??= s.World!.CreatePocketSystem(EntryDef, "cluster", s.Anchor);
                return Ready(s.Entry);
            }),
            new TestStep("link entry door", "IWorldProvider.CreateWormholePair", () =>
            {
                s.EntryDoor ??= s.World!.CreateWormholePair(EntryDoorDef, "cluster-door", s.Anchor, SystemOf(s.Entry));
                return Ready(s.EntryDoor);
            }),
            new TestStep("create hub pocket", "CreatePocketSystem", () =>
            {
                s.Hub ??= s.World!.CreatePocketSystem(HubDef, "hub", SystemOf(s.Entry));
                return Ready(s.Hub);
            }),
            new TestStep("create anchor pocket", "CreatePocketSystem", () =>
            {
                s.AnchorSystem ??= s.World!.CreatePocketSystem(AnchorDef, "anchor", SystemOf(s.Entry));
                return Ready(s.AnchorSystem);
            }),
            new TestStep("create mining pocket", "CreatePocketSystem", () =>
            {
                s.Mining ??= s.World!.CreatePocketSystem(MiningDef, "mining", SystemOf(s.Hub));
                return Ready(s.Mining);
            }),
            new TestStep("link mining hole", "CreateWormholePair", () =>
            {
                s.MiningHole ??= s.World!.CreateWormholePair(MiningWormholeDef, "mining-hole", SystemOf(s.Hub), SystemOf(s.Mining));
                return Ready(s.MiningHole);
            }),
            new TestStep("create mining site", "CreateResourceSite", () =>
            {
                s.MiningSite ??= s.World!.CreateResourceSite(MiningSiteDef, "mining-site", SystemOf(s.Mining), 0f, 0f);
                return Ready(s.MiningSite);
            }),
            new TestStep("create salvage pocket", "CreatePocketSystem", () =>
            {
                s.Salvage ??= s.World!.CreatePocketSystem(SalvageDef, "salvage", SystemOf(s.Hub));
                return Ready(s.Salvage);
            }),
            new TestStep("link salvage hole", "CreateWormholePair", () =>
            {
                s.SalvageHole ??= s.World!.CreateWormholePair(SalvageWormholeDef, "salvage-hole", SystemOf(s.Hub), SystemOf(s.Salvage));
                return Ready(s.SalvageHole);
            }),
            new TestStep("create salvage site", "CreateResourceSite", () =>
            {
                s.SalvageSite ??= s.World!.CreateResourceSite(SalvageSiteDef, "salvage-site", SystemOf(s.Salvage), 0f, 0f);
                return Ready(s.SalvageSite);
            }),
            new TestStep("open gates and rifts", "IWormholePair.SetOpen / IPocketSystem.SetEntranceOpen", () =>
            {
                if (!OpenPair(s.EntryDoor) || !OpenPair(s.MiningHole) || !OpenPair(s.SalvageHole)) return false;
                if (!OpenPocketGate(s.Hub) || !OpenPocketGate(s.AnchorSystem)) return false;
                return true;
            }),
            new TestStep("assert authored topology", "Pocket/Wormhole/Site reconstruction + wiring", () =>
            {
                var w = s.World!;
                AssertName(s.Entry, EntryName, "entry");
                AssertName(s.Hub, HubName, "hub");
                AssertName(s.AnchorSystem, AnchorName, "anchor");
                AssertName(s.Mining, MiningWorldName, "mining");
                AssertName(s.Salvage, SalvageWorldName, "salvage");
                if (!Ready(s.EntryDoor) || !Ready(s.MiningHole) || !Ready(s.SalvageHole))
                    throw new InvalidOperationException("A wormhole pair failed to reconstruct.");
                if (!Ready(s.MiningSite) || !Ready(s.SalvageSite))
                    throw new InvalidOperationException("A resource site failed to reconstruct.");
                foreach (var def in new[] { EntryDef, HubDef, AnchorDef, MiningDef, SalvageDef })
                    if (w.GetPocketSystems(def).Count != 1) throw new InvalidOperationException("Pocket count for " + def + " != 1.");
                foreach (var def in new[] { EntryDoorDef, MiningWormholeDef, SalvageWormholeDef })
                    if (w.GetWormholePairs(def).Count != 1) throw new InvalidOperationException("Wormhole count for " + def + " != 1.");
                foreach (var def in new[] { MiningSiteDef, SalvageSiteDef })
                    if (w.GetResourceSites(def).Count != 1) throw new InvalidOperationException("Site count for " + def + " != 1.");
                if (ModApi.Services.Travel.CurrentLocation?.SystemId != s.Anchor)
                    throw new InvalidOperationException("Player unexpectedly left the anchor system.");
                return true;
            }),
            new TestStep("wormhole-endpoint removal guard", "IPocketSystem.CanRemove / RemovalStatus.WormholeEndpoint", () =>
            {
                foreach (var (label, p) in new[] { ("mining", s.Mining), ("hub", s.Hub), ("entry", s.Entry) })
                    if (p!.CanRemove() != RemovalStatus.WormholeEndpoint)
                        throw new InvalidOperationException(label + " CanRemove was not WormholeEndpoint while a pair uses it.");
                return true;
            }),
            new TestStep("cleanup wormhole pairs", "IWormholePair.Remove (pairs first)", () =>
            {
                RemovePair(s.MiningHole, "mining-hole"); s.MiningHole = null;
                RemovePair(s.SalvageHole, "salvage-hole"); s.SalvageHole = null;
                RemovePair(s.EntryDoor, "cluster-door"); s.EntryDoor = null;
                return true;
            }),
            new TestStep("cleanup pockets", "IPocketSystem.Remove (sites go with pocket)", () =>
            {
                RemovePocket(s.Mining, "mining"); s.Mining = null; s.MiningSite = null;
                RemovePocket(s.Salvage, "salvage"); s.Salvage = null; s.SalvageSite = null;
                RemovePocket(s.Hub, "hub"); s.Hub = null;
                RemovePocket(s.AnchorSystem, "anchor"); s.AnchorSystem = null;
                RemovePocket(s.Entry, "entry"); s.Entry = null;
                return true;
            }),
            new TestStep("assert cluster fully gone", "GetPocketSystems / GetWormholePairs / GetResourceSites", () =>
            {
                var w = s.World!;
                foreach (var def in new[] { EntryDef, HubDef, AnchorDef, MiningDef, SalvageDef })
                    if (w.GetPocketSystems(def).Count != 0) throw new InvalidOperationException("Pocket " + def + " still present after cleanup.");
                foreach (var def in new[] { EntryDoorDef, MiningWormholeDef, SalvageWormholeDef })
                    if (w.GetWormholePairs(def).Count != 0) throw new InvalidOperationException("Wormhole " + def + " still present after cleanup.");
                foreach (var def in new[] { MiningSiteDef, SalvageSiteDef })
                    if (w.GetResourceSites(def).Count != 0) throw new InvalidOperationException("Site " + def + " still present after cleanup.");
                if (ModApi.Services.Travel.CurrentLocation?.SystemId != s.Anchor)
                    throw new InvalidOperationException("Cleanup moved the player away from the anchor system.");
                if (events.Any(e => e.Kind == LifecycleEventKind.SaveSucceeded))
                    throw new InvalidOperationException("An ephemeral player unexpectedly saved.");
                return true;
            }),
        };
    }

    private sealed class WorldState
    {
        internal IWorldProvider? World;
        internal string Anchor = "";
        internal IPocketSystem? Entry;
        internal IPocketSystem? Hub;
        internal IPocketSystem? AnchorSystem;
        internal IPocketSystem? Mining;
        internal IPocketSystem? Salvage;
        internal IWormholePair? EntryDoor;
        internal IWormholePair? MiningHole;
        internal IWormholePair? SalvageHole;
        internal IResourceSite? MiningSite;
        internal IResourceSite? SalvageSite;
    }

    private static bool Ready(IPocketSystem? p) => p != null && p.State.Reconstructed && p.SystemId != null;
    private static bool Ready(IWormholePair? w) => w != null && w.State.Reconstructed
        && w.FirstWormholePoiId != null && w.SecondWormholePoiId != null;
    private static bool Ready(IResourceSite? r) => r != null && r.State.Reconstructed && r.PoiId != null;

    private static string SystemOf(IPocketSystem? p)
        => Ready(p) ? p!.SystemId! : throw new InvalidOperationException("Dependency pocket is not reconstructed yet.");

    private static void AssertName(IPocketSystem? p, string expected, string label)
    {
        if (!Ready(p)) throw new InvalidOperationException(label + " pocket is not reconstructed.");
        if (!string.Equals(p!.Definition.Name, expected, StringComparison.Ordinal))
            throw new InvalidOperationException(label + " pocket name '" + p.Definition.Name + "' != '" + expected + "'.");
    }

    // Gate/rift opening may transiently be NotReady while reconstruction completes; retry via `false`
    // instead of failing, so a durable refusal surfaces only through the deadline.
    private static bool OpenPair(IWormholePair? pair) => pair != null && Ready(pair) && pair.SetOpen(true).Succeeded;
    private static bool OpenPocketGate(IPocketSystem? p) => p != null && Ready(p) && p.SetEntranceOpen(true).Succeeded;

    private static void RemovePair(IWormholePair? pair, string label)
    {
        if (pair == null) return;
        var result = pair.Remove();
        if (!result.Succeeded) throw new InvalidOperationException("wormhole " + label + " remove: " + result.Status + " - " + result.Detail);
        if (pair.State.Status != ReconstructionStatus.Removed) throw new InvalidOperationException("wormhole " + label + " not terminal after Remove.");
    }

    private static void RemovePocket(IPocketSystem? pocket, string label)
    {
        if (pocket == null) return;
        var result = pocket.Remove();
        if (!result.Succeeded) throw new InvalidOperationException("pocket " + label + " remove: " + result.Status + " - " + result.Detail);
        if (pocket.State.Status != ReconstructionStatus.Removed) throw new InvalidOperationException("pocket " + label + " not terminal after Remove.");
    }
}
