using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.E2E;

/// <summary>Gameplay E2E for the actual examples/PocketWorlds plugin. It clicks the rendered HUD,
/// follows real TravelManager routes through every connection and site, then clicks deletion.</summary>
internal static class PocketWorldsCase
{
    internal const string Id = "pocket-worlds";

    internal static IReadOnlyList<TestStep> Steps(ILifecycleService lifecycle, List<LifecycleEvent> lifecycleEvents,
        List<TravelTransition> travelEvents)
    {
        var s = new State();
        var result = new List<TestStep>(LiveBoot.Steps("vgmodapi.example.pocket-worlds", lifecycle, lifecycleEvents))
        {
            new TestStep("observe initial placement and HUD", "ITravelService.CurrentLocation / Mod API shared HUD", 30, () =>
            {
                s.Plugin ??= NativeGameplay.ExamplePlugin();
                var location = ModApi.Services.Travel.CurrentLocation;
                if (s.Plugin == null || location?.PoiId == null)
                    return StepResult.Wait($"pluginLoaded={s.Plugin != null}; location={location?.SystemId ?? "null"}/{location?.PoiId ?? "null"}");
                s.OriginSystem = location.SystemId;
                s.OriginPoi = location.PoiId;
                NativeGameplay.Screenshot("initial-placement");
                return StepResult.Pass($"origin={s.OriginSystem}/{s.OriginPoi}");
            }),
            new TestStep("click Spawn Wormhole", "UnityEngine.UI.Button.onClick / PocketWorlds.OnHud(spawn)", () =>
            {
                NativeSession.RequireEphemeral();
                return NativeGameplay.ClickHudRow("Spawn Wormhole");
            }),
            new TestStep("wait for actual example topology", "PocketWorlds.SpawnCluster / owned poi handles", () =>
            {
                Capture(s);
                if (!s.AllReady) return false;
                AssertTopology(s);
                NativeGameplay.Screenshot("cluster-authored");
                return true;
            }),
            new TestStep("verify sealed and open connections", "JumpGate.canUseJumpGate / hidden / Wormhole.canUseWormhole", () =>
            {
                AssertConnectionState(s);
                return true;
            }),
            new TestStep("click Log topology", "UnityEngine.UI.Button.onClick / PocketWorlds.OnHud(log)", () =>
                NativeGameplay.ClickHudRow("Log topology")),

            // Real routed gameplay: each request enters the destination scene and waits for the API's
            // observed current location. No player/currentSystem field is assigned by the harness.
            TravelStep("travel X to Cluster Entry", s, () => s.EntryDoor!.SecondWormholePoiId!, () => s.Entry!.SystemId!, TravelMode.Wormhole, travelEvents),
            new TestStep("refuse Delete Cluster while inside", "PocketWorlds.OnHud(delete) / CanRemove", () =>
            {
                if (!NativeGameplay.ClickHudRow("Delete Cluster")) return false;
                foreach (var id in s.NativePoiIds)
                    if (NativeGameplay.Poi(id) == null) throw new InvalidOperationException("Delete Cluster partially removed content while the player was inside: " + id);
                if (FieldsCleared(s.Plugin!)) throw new InvalidOperationException("Delete Cluster claimed success while the player was inside.");
                NativeGameplay.Screenshot("delete-refused-inside-cluster");
                return true;
            }),
            TravelStep("travel Entry to Hub Alpha", s, () => s.Hub!.PocketGatePoiId!, () => s.Hub!.SystemId!, TravelMode.JumpGate, travelEvents),
            TravelStep("travel Hub to Mining Instance", s, () => s.MiningHole!.SecondWormholePoiId!, () => s.Mining!.SystemId!, TravelMode.Wormhole, travelEvents),
            TravelStep("visit mining field", s, () => s.MiningSite!.PoiId!, () => s.Mining!.SystemId!, TravelMode.InSystem, travelEvents),
            TravelStep("return Mining to Hub", s, () => s.MiningHole!.FirstWormholePoiId!, () => s.Hub!.SystemId!, TravelMode.Wormhole, travelEvents),
            TravelStep("travel Hub to Salvage Instance", s, () => s.SalvageHole!.SecondWormholePoiId!, () => s.Salvage!.SystemId!, TravelMode.Wormhole, travelEvents),
            TravelStep("visit salvage wreck", s, () => s.SalvageSite!.PoiId!, () => s.Salvage!.SystemId!, TravelMode.InSystem, travelEvents),
            TestStep.ActionThenWait("attach dungeon to salvage station",
                "PocketWorlds.Attach(IResourceSite) / IDungeonProvider.GetDungeons", 30,
                () => NativeGameplay.ClickHudRow("Attach station layout")
                    ? StepResult.Pass("Attach station layout clicked at " + LocationObservation())
                    : StepResult.Wait("Attach station layout is not clickable yet; " + LocationObservation()),
                () =>
                {
                    s.DungeonProvider ??= NativeGameplay.Field<IDungeonProvider>(s.Plugin!, "_dungeonProvider");
                    s.DungeonId ??= NativeGameplay.GetField(s.Plugin!, "_salvageDungeonId") as Guid?;
                    var last = NativeGameplay.GetField(s.Plugin!, "_lastDungeonAttach") as string ?? "no attach result";
                    var observed = ModApi.Services.Dungeons.GetTargets().Count;
                    if (s.DungeonProvider == null || !s.DungeonId.HasValue)
                        return StepResult.Wait(last + "; observed targets=" + observed + "; " + LocationObservation());
                    if (!s.DungeonProvider.GetDungeons().Any(dungeon => dungeon.Id == s.DungeonId.Value))
                        return StepResult.Fail("Attach returned dungeon " + s.DungeonId.Value + " but GetDungeons omitted it; " + last);
                    return StepResult.Pass("Retained dungeon " + s.DungeonId.Value + "; " + last);
                }),
            TravelStep("return Salvage to Hub", s, () => s.SalvageHole!.FirstWormholePoiId!, () => s.Hub!.SystemId!, TravelMode.Wormhole, travelEvents),
            TravelStep("return Hub to Entry", s, () => s.Hub!.EntranceGatePoiId!, () => s.Entry!.SystemId!, TravelMode.JumpGate, travelEvents),
            TravelStep("travel Entry to Anchor Beta", s, () => s.Anchor!.PocketGatePoiId!, () => s.Anchor!.SystemId!, TravelMode.JumpGate, travelEvents),
            TravelStep("return Anchor to Entry", s, () => s.Anchor!.EntranceGatePoiId!, () => s.Entry!.SystemId!, TravelMode.JumpGate, travelEvents),
            TravelStep("return Entry to X", s, () => s.EntryDoor!.FirstWormholePoiId!, () => s.OriginSystem, TravelMode.Wormhole, travelEvents),
            TravelStep("leave entry rift at original POI", s, () => s.OriginPoi, () => s.OriginSystem, TravelMode.InSystem, travelEvents),

            new TestStep("click Delete Cluster", "UnityEngine.UI.Button.onClick / PocketWorlds.OnHud(delete)", () =>
            {
                NativeSession.RequireEphemeral();
                if (ModApi.Services.Travel.CurrentLocation?.SystemId != s.OriginSystem) return false;
                return NativeGameplay.ClickHudRow("Delete Cluster");
            }),
            new TestStep("assert native and API cleanup", "PocketWorlds.DeleteCluster / GalaxyMapData.GetPointOfInterest", () =>
            {
                if (!FieldsCleared(s.Plugin!)) return false;
                foreach (var id in s.NativePoiIds)
                    if (NativeGameplay.Poi(id) != null) throw new InvalidOperationException("Native POI still exists after Delete Cluster: " + id);
                foreach (var pocket in s.Pockets)
                    if (pocket.State.Status != ReconstructionStatus.Removed) throw new InvalidOperationException(pocket.PoiKey + " was not removed.");
                foreach (var pair in s.Pairs)
                    if (pair.State.Status != ReconstructionStatus.Removed) throw new InvalidOperationException(pair.PoiKey + " was not removed.");
                foreach (var site in s.Sites)
                    if (site.State.Status != ReconstructionStatus.Removed) throw new InvalidOperationException(site.PoiKey + " was not removed with its pocket.");
                if (s.DungeonProvider == null || !s.DungeonId.HasValue
                    || s.DungeonProvider.GetDungeons().Any(dungeon => dungeon.Id == s.DungeonId.Value))
                    throw new InvalidOperationException("The removed salvage pocket left its attached dungeon in retained API state.");
                if (lifecycleEvents.Any(e => e.Kind == LifecycleEventKind.SaveSucceeded))
                    throw new InvalidOperationException("An ephemeral player unexpectedly saved.");
                NativeGameplay.Screenshot("cluster-deleted");
                return true;
            }),
        };
        return result;
    }

    private static string LocationObservation()
    {
        var current = ModApi.Services.Travel.CurrentLocation;
        return "current system=" + (current?.SystemId ?? "null") + ", poi=" + (current?.PoiId ?? "null");
    }

    private static TestStep TravelStep(string name, State s, Func<string> poi, Func<string> system,
        TravelMode mode, List<TravelTransition> events)
    {
        var firstSequence = 0L;
        return TestStep.ActionThenWait(name, "ITravelService.RequestRoute / Transitioned", 180,
            () =>
            {
                NativeSession.RequireEphemeral();
                firstSequence = events.Count == 0 ? 0 : events.Max(e => e.Sequence);
                var result = ModApi.Services.Travel.RequestRoute(poi(), 7f);
                return result.Accepted ? StepResult.Pass("route accepted")
                    : StepResult.Fail("Travel request refused: " + result.Status + " - " + result.Detail);
            },
            () =>
            {
                var targetPoi = poi();
                var targetSystem = system();
                var current = ModApi.Services.Travel.CurrentLocation;
                var leg = events.Where(e => e.Sequence > firstSequence).ToArray();
                var arrived = leg.Any(e => e.Kind == TravelTransitionKind.Arrived && e.Mode == mode
                    && e.ActualLocation?.SystemId == targetSystem && e.ActualLocation.PoiId == targetPoi);
                var completed = leg.Any(e => e.Kind == TravelTransitionKind.RouteCompleted && e.Mode == mode);
                var observation = $"current={current?.SystemId ?? "null"}/{current?.PoiId ?? "null"}; "
                    + $"target={targetSystem}/{targetPoi}; arrived={arrived}; completed={completed}; transitions={leg.Length}";
                if (current?.SystemId != targetSystem || current.PoiId != targetPoi || !arrived || !completed)
                    return StepResult.Wait(observation);
                NativeGameplay.Screenshot(name);
                return StepResult.Pass(observation);
            });
    }

    private static void Capture(State s)
    {
        var p = s.Plugin!;
        s.Entry ??= NativeGameplay.Field<IPocketSystem>(p, "_entry");
        s.Hub ??= NativeGameplay.Field<IPocketSystem>(p, "_hub");
        s.Anchor ??= NativeGameplay.Field<IPocketSystem>(p, "_anchor");
        s.Mining ??= NativeGameplay.Field<IPocketSystem>(p, "_mining");
        s.Salvage ??= NativeGameplay.Field<IPocketSystem>(p, "_salvage");
        s.EntryDoor ??= NativeGameplay.Field<IWormholePair>(p, "_entryDoor");
        s.MiningHole ??= NativeGameplay.Field<IWormholePair>(p, "_miningHole");
        s.SalvageHole ??= NativeGameplay.Field<IWormholePair>(p, "_salvageHole");
        s.MiningSite ??= NativeGameplay.Field<IResourceSite>(p, "_miningSite");
        s.SalvageSite ??= NativeGameplay.Field<IResourceSite>(p, "_salvageSite");
        if (s.AllReady && s.NativePoiIds.Count == 0)
        {
            s.NativePoiIds.AddRange(s.Pockets.SelectMany(x => new[] { x.EntranceGatePoiId!, x.PocketGatePoiId! }));
            s.NativePoiIds.AddRange(s.Pairs.SelectMany(x => new[] { x.FirstWormholePoiId!, x.SecondWormholePoiId! }));
            s.NativePoiIds.AddRange(s.Sites.Select(x => x.PoiId!));
        }
    }

    private static void AssertTopology(State s)
    {
        var expected = new[] { "Cluster Entry", "Hub Alpha", "Anchor Beta", "Mining Instance", "Salvage Instance" };
        if (!s.Pockets.Select(p => p.Definition.Name).SequenceEqual(expected))
            throw new InvalidOperationException("Actual example pocket names/topology differ from the documented topology.");
        AssertPair(s.EntryDoor!, s.OriginSystem, s.Entry!.SystemId!);
        AssertPair(s.MiningHole!, s.Hub!.SystemId!, s.Mining!.SystemId!);
        AssertPair(s.SalvageHole!, s.Hub.SystemId!, s.Salvage!.SystemId!);
        if (NativeGameplay.SystemId(NativeGameplay.Poi(s.MiningSite!.PoiId!)!) != s.Mining.SystemId
            || NativeGameplay.SystemId(NativeGameplay.Poi(s.SalvageSite!.PoiId!)!) != s.Salvage.SystemId)
            throw new InvalidOperationException("A resource site was authored in the wrong system.");
    }

    private static void AssertPair(IWormholePair pair, string firstSystem, string secondSystem)
    {
        var first = NativeGameplay.Poi(pair.FirstWormholePoiId!)!;
        var second = NativeGameplay.Poi(pair.SecondWormholePoiId!)!;
        if (NativeGameplay.SystemId(first) != firstSystem || NativeGameplay.SystemId(second) != secondSystem)
            throw new InvalidOperationException(pair.PoiKey + " endpoints lead to the wrong systems.");
        if (NativeGameplay.StringListCount(first, "targetWormholeGuids") != 1
            || NativeGameplay.StringListCount(second, "targetWormholeGuids") != 1)
            throw new InvalidOperationException(pair.PoiKey + " is not an exact one-to-one wormhole pair.");
    }

    private static void AssertConnectionState(State s)
    {
        // Entry's ordinary anchor gate is deliberately sealed+hidden; the wormhole is the only door.
        foreach (var id in new[] { s.Entry!.EntranceGatePoiId!, s.Entry.PocketGatePoiId! })
        {
            var gate = NativeGameplay.Poi(id)!;
            if (!NativeGameplay.Bool(gate, "hidden") || NativeGameplay.Bool(gate, "canUseJumpGate"))
                throw new InvalidOperationException("Entry anchor gate is not sealed and hidden: " + id);
        }
        foreach (var id in new[] { s.Hub!.EntranceGatePoiId!, s.Hub.PocketGatePoiId!, s.Anchor!.EntranceGatePoiId!, s.Anchor.PocketGatePoiId! })
        {
            var gate = NativeGameplay.Poi(id)!;
            if (NativeGameplay.Bool(gate, "hidden") || !NativeGameplay.Bool(gate, "canUseJumpGate"))
                throw new InvalidOperationException("Deliberate cluster gate is not open and visible: " + id);
        }
        foreach (var pair in s.Pairs)
            foreach (var id in new[] { pair.FirstWormholePoiId!, pair.SecondWormholePoiId! })
                if (!NativeGameplay.Bool(NativeGameplay.Poi(id)!, "canUseWormhole"))
                    throw new InvalidOperationException("Wormhole is not open/usable: " + id);
    }

    private static bool FieldsCleared(object plugin) => new[] { "_entry", "_hub", "_anchor", "_mining", "_salvage",
        "_entryDoor", "_miningHole", "_salvageHole", "_miningSite", "_salvageSite" }
        .All(name => plugin.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(plugin) == null);

    private sealed class State
    {
        internal object? Plugin;
        internal string OriginSystem = "";
        internal string OriginPoi = "";
        internal IPocketSystem? Entry, Hub, Anchor, Mining, Salvage;
        internal IWormholePair? EntryDoor, MiningHole, SalvageHole;
        internal IResourceSite? MiningSite, SalvageSite;
        internal IDungeonProvider? DungeonProvider;
        internal Guid? DungeonId;
        internal readonly List<string> NativePoiIds = new();
        internal IPocketSystem[] Pockets => new[] { Entry!, Hub!, Anchor!, Mining!, Salvage! };
        internal IWormholePair[] Pairs => new[] { EntryDoor!, MiningHole!, SalvageHole! };
        internal IResourceSite[] Sites => new[] { MiningSite!, SalvageSite! };
        internal bool AllReady => Pockets.All(p => p != null && p.State.Reconstructed && p.SystemId != null
                && p.EntranceGatePoiId != null && p.PocketGatePoiId != null)
            && Pairs.All(p => p != null && p.State.Reconstructed && p.FirstWormholePoiId != null && p.SecondWormholePoiId != null)
            && Sites.All(p => p != null && p.State.Reconstructed && p.PoiId != null);
    }
}
