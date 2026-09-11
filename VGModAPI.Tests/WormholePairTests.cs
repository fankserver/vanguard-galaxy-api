using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

internal sealed class FakeWormholePairs : IWormholePairNative
{
    internal int Next, Creates, Applies; internal bool FailCreate; internal int Ambiguity = 1;
    internal readonly Dictionary<string, (string A, string B, string ASys, string BSys, bool Open)> Pairs = new();
    public WormholePairInfo? CreatePair(Guid session, string name, string a, string b, bool open)
    {
        Creates++; if (FailCreate || a == b) return null; string key = "pair-" + ++Next;
        var pair = ("wa-" + Next, "wb-" + Next, a, b, open); Pairs[key] = pair; return new(pair.Item1, pair.Item2, open);
    }
    public WormholePairInfo? ResolvePair(Guid session, string a, string b, string wa, string wb)
    {
        foreach (var pair in Pairs.Values) if (pair.A == wa && pair.B == wb && pair.ASys == a && pair.BSys == b) return new(wa, wb, pair.Open);
        return null;
    }
    public int AmbiguousCount(Guid session, string poiId) => Pairs.Values.Any(p => p.A == poiId || p.B == poiId) ? Ambiguity : 0;
    public bool ApplyOpen(Guid session, string wa, string wb, bool open)
    {
        Applies++; foreach (var entry in Pairs.ToArray()) if (entry.Value.A == wa && entry.Value.B == wb)
        { Pairs[entry.Key] = (entry.Value.A, entry.Value.B, entry.Value.ASys, entry.Value.BSys, open); return true; }
        return false;
    }
    public void BeginPass(Guid session) { }
    public void EndPass() { }
}

public sealed class WormholePairTests
{
    private sealed class Harness : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, e) => throw e);
        internal readonly FakeWormholePairs Native = new();
        internal readonly WorldDefinitionRegistry Combat;
        internal readonly WormholePairRegistry Definitions;
        internal readonly WormholePairCoordinator Coordinator;
        internal readonly WorldContentService Service;
        internal readonly IWorldProvider Provider;
        internal Guid Session;
        internal Harness()
        {
            var plugin = new object(); StoryHostAuthenticator auth = (o, a) => ReferenceEquals(o, plugin) ? new StoryHostPlugin("author.a", a) : null;
            Combat = new(auth, Hub.CheckThread); Definitions = new(auth, Hub.CheckThread);
            Coordinator = new(Hub, Definitions, Native, _ => true, _ => { });
            Service = new(Hub, Combat, null!, () => true, wormholeDefinitions: Definitions, wormholeCoordinator: Coordinator);
            Provider = Service.AcquireProvider(plugin)!;
        }
        internal void Begin() { Session = Hub.Begin(SessionOrigin.NewGame, null); Hub.PlayerReady(Session); Hub.GameplayInitialized(Session); }
        public void Dispose() { Provider.Dispose(); Coordinator.Dispose(); Service.Dispose(); Definitions.Dispose(); Combat.Dispose(); Hub.Dispose(); }
    }

    [Fact]
    public void DefinitionAndCreationAreModderKeyedDomainObjects()
    {
        using var h = new Harness();
        Assert.Equal(WorldStatus.Succeeded, h.Provider.RegisterWormholePair(new("rift", 1, "Unstable Rift")));
        Assert.Equal(WorldStatus.DuplicateDefinition, h.Provider.RegisterWormholePair(new("rift", 1, "Duplicate")));
        h.Begin();
        var first = h.Provider.CreateWormholePair("rift", "daily", "system-a", "system-b")!;
        Assert.True(first.State.Reconstructed); Assert.Equal("wa-1", first.FirstWormholePoiId); Assert.Equal("wb-1", first.SecondWormholePoiId);
        Assert.Same(first, h.Provider.CreateWormholePair("rift", "daily", "system-a", "system-b"));
        Assert.Same(first, h.Provider.GetWormholePair("rift", "daily"));
        Assert.Same(first, Assert.Single(h.Provider.GetWormholePairs("rift")));
        Assert.Equal(1, h.Native.Creates);
    }

    [Fact]
    public void PairRequiresTwoDistinctSystemsAndAReadySession()
    {
        using var h = new Harness(); h.Provider.RegisterWormholePair(new("rift", 1, "Rift"));
        Assert.Null(h.Provider.CreateWormholePair("rift", "k", "a", "b")); h.Begin();
        Assert.Null(h.Provider.CreateWormholePair("rift", "same", "a", "a"));
        h.Native.FailCreate = true; Assert.Null(h.Provider.CreateWormholePair("rift", "failed", "a", "b"));
    }

    [Fact]
    public void SetOpenControlsBothOwnedEndsAndIsReconciled()
    {
        using var h = new Harness(); h.Provider.RegisterWormholePair(new("rift", 1, "Rift")); h.Begin();
        var pair = h.Provider.CreateWormholePair("rift", "k", "a", "b")!;
        Assert.True(pair.SetOpen(false).Succeeded); Assert.False(Assert.Single(h.Native.Pairs).Value.Open);
        // Native drift converges to the retained declaration.
        var row = Assert.Single(h.Native.Pairs); h.Native.Pairs[row.Key] = (row.Value.A, row.Value.B, row.Value.ASys, row.Value.BSys, true);
        h.Coordinator.Reconcile(h.Session); Assert.False(Assert.Single(h.Native.Pairs).Value.Open);
    }

    [Fact]
    public void SetOpenNeverAdoptsARepointedOrMovedPair()
    {
        using var h = new Harness(); h.Provider.RegisterWormholePair(new("rift", 1, "Rift")); h.Begin();
        var pair = h.Provider.CreateWormholePair("rift", "k", "a", "b")!;
        var row = Assert.Single(h.Native.Pairs);
        h.Native.Pairs[row.Key] = (row.Value.A, row.Value.B, "foreign-system", row.Value.BSys, row.Value.Open);
        Assert.Equal(WorldContentStatus.Rejected, pair.SetOpen(false).Status);
        Assert.True(Assert.Single(h.Native.Pairs).Value.Open);
    }

    [Fact]
    public void SavedPairRoundTripsWithExactSystemsPoiIdsAndGateState()
    {
        var original = new WormholePairOccurrence("author.a", "rift", "k", 2, "a", "b", "wa", "wb", false);
        byte[] bytes = PocketSystemStateCodec.Encode(Array.Empty<PocketSystemOccurrence>(), Array.Empty<ResourceSiteOccurrence>(), Array.Empty<MooredShipOccurrence>(), new[] { original });
        var decoded = Assert.Single(PocketSystemStateCodec.DecodeAll(bytes).Wormholes);
        Assert.Equal(original.Owner, decoded.Owner); Assert.Equal(original.LocalId, decoded.LocalId); Assert.Equal(original.OccurrenceKey, decoded.OccurrenceKey);
        Assert.Equal("a", decoded.FirstSystemId); Assert.Equal("b", decoded.SecondSystemId); Assert.Equal("wa", decoded.FirstPoiId); Assert.Equal("wb", decoded.SecondPoiId); Assert.False(decoded.DeclaredOpen);
    }

    [Fact]
    public void MissingAndAmbiguousNativeIdentityAreHonestStates()
    {
        using var h = new Harness(); h.Provider.RegisterWormholePair(new("rift", 1, "Rift")); h.Begin();
        var pair = h.Provider.CreateWormholePair("rift", "k", "a", "b")!;
        h.Native.Ambiguity = 2; h.Service.MaintainPocketSystems(h.Session); Assert.Equal(WormholePairFailureReason.AmbiguousIdentity, pair.State.Reason);
        h.Native.Ambiguity = 1; h.Native.Pairs.Clear(); h.Service.MaintainPocketSystems(h.Session); Assert.Equal(ReconstructionStatus.Pending, pair.State.Status);
    }

    [Fact]
    public void StaleSessionObjectCannotControlReplacementSave()
    {
        using var h = new Harness(); h.Provider.RegisterWormholePair(new("rift", 1, "Rift")); h.Begin();
        var pair = h.Provider.CreateWormholePair("rift", "k", "a", "b")!; int applies = h.Native.Applies;
        h.Hub.Invalidate("test"); h.Begin();
        Assert.Equal(WorldContentStatus.GameEnded, pair.SetOpen(false).Status); Assert.Equal(applies, h.Native.Applies);
    }
}
