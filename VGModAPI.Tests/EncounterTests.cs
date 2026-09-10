using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class EncounterTests
{
    private sealed class FakeEncounters : IEncounterNative
    {
        internal readonly List<(string Poi, EncounterComposition Composition)> Spawned = new();
        internal Func<string, EncounterComposition, (int, string)?>? Handler;
        public (int Scheduled, string Detail)? Spawn(Guid session, string poiId, EncounterComposition composition)
        {
            Spawned.Add((poiId, composition));
            if (Handler != null) return Handler(poiId, composition);
            int total = 0; foreach (var wave in composition.Waves) total += wave.Count;
            return (total, "");
        }
    }
    private sealed class Harness : IDisposable
    {
        internal readonly LifecycleHub Hub;
        internal readonly FakeEncounters Native = new();
        internal readonly WorldDefinitionRegistry Combat;
        internal readonly WorldContentService Service;
        internal IWorldProvider Provider = null!;
        internal Guid Session;
        internal Harness(Func<bool>? canAuthor = null)
        {
            Hub = new LifecycleHub((_, error) => throw error);
            var plugin = new object();
            StoryHostAuthenticator auth = (instance, caller) => ReferenceEquals(instance, plugin) ? new StoryHostPlugin("author.a", caller) : null;
            Combat = new WorldDefinitionRegistry(auth, Hub.CheckThread);
            Service = new WorldContentService(Hub, Combat, null!, canAuthor ?? (() => true), null, null, null, null, null, null, null, null, null, null, Native);
            Provider = Service.AcquireProvider(plugin)!;
        }
        internal void BeginGameplay()
        {
            Session = Hub.Begin(SessionOrigin.NewGame, null);
            Hub.PlayerReady(Session); Hub.GameplayInitialized(Session);
        }
        public void Dispose() { Provider?.Dispose(); Service.Dispose(); Combat.Dispose(); Hub.Dispose(); }
    }
    private static EncounterComposition Ambush(int waves = 2, int perWave = 10) => new(
        Enumerable.Range(0, waves).Select(index => new EncounterWave(15 + index * 20, "Monsoon", perWave)),
        "Fanatics", level: 12, EncounterRank.Standard, hostileToPlayer: true);

    [Fact]
    public void CompositionValidationEnforcesExactContract()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncounterWave(-1, "a", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncounterWave(0, "a", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncounterWave(0, "a", 51));
        Assert.Throws<ArgumentException>(() => new EncounterWave(0, " ", 1));
        Assert.Throws<ArgumentException>(() => new EncounterComposition(Array.Empty<EncounterWave>(), "f", 1));
        Assert.Throws<ArgumentException>(() => new EncounterComposition(
            Enumerable.Range(0, 5).Select(_ => new EncounterWave(0, "a", 50)), "f", 1)); // 250 > 200 total
        Assert.Equal(20, Ambush().Waves.Sum(wave => wave.Count));
    }

    [Fact]
    public void SpawnIsGameplayGatedAndReportsExactScheduling()
    {
        using var h = new Harness();
        Assert.Equal(AuthoredActionStatus.NotReady, h.Provider.SpawnEncounter("poi", Ambush()).Status);
        h.BeginGameplay();
        var result = h.Provider.SpawnEncounter("poi", Ambush());
        Assert.True(result.Succeeded);
        Assert.Equal(20, result.ScheduledUnits);
        Assert.Single(h.Native.Spawned);
        Assert.Throws<ArgumentNullException>(() => h.Provider.SpawnEncounter("poi", null!));
        Assert.Equal(AuthoredActionStatus.Rejected, h.Provider.SpawnEncounter(" ", Ambush()).Status);
    }

    [Fact]
    public void PartialOrRefusedNativeSchedulingIsAnHonestRejection()
    {
        using var h = new Harness();
        h.BeginGameplay();
        h.Native.Handler = (_, _) => (4, "");
        var partial = h.Provider.SpawnEncounter("poi", Ambush());
        Assert.Equal(AuthoredActionStatus.Rejected, partial.Status);
        Assert.Equal(4, partial.ScheduledUnits);
        h.Native.Handler = (_, _) => (0, "Unknown ship class: Bogus");
        var unknown = h.Provider.SpawnEncounter("poi", Ambush());
        Assert.Equal(AuthoredActionStatus.Rejected, unknown.Status);
        Assert.Contains("Bogus", unknown.Detail);
        h.Native.Handler = (_, _) => null;
        Assert.Equal(AuthoredActionStatus.Unavailable, h.Provider.SpawnEncounter("poi", Ambush()).Status);
    }

    [Fact]
    public void UnavailableAuthoringRefusesWithoutTouchingTheNativeSeam()
    {
        using var h = new Harness(canAuthor: () => false);
        h.BeginGameplay();
        Assert.Equal(AuthoredActionStatus.Unavailable, h.Provider.SpawnEncounter("poi", Ambush()).Status);
        Assert.Empty(h.Native.Spawned);
    }
}
