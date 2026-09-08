using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonContentServiceTests
{
    private sealed class Persistence : TestSaveDataService
    {
        internal PersistenceProvider Provider = null!;
        public bool MutationAllowed => true;
        public bool StateReady => true;
        public string Status => "ready";
        public override bool CanRead => StateReady;
        public override bool CanMutate => StateReady && MutationAllowed;
        public override SaveDataRegistrationResult Register(PersistenceProvider provider) { Provider = provider; return new(SaveDataRegistrationStatus.Registered, this); }
        public override void Dispose() { }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly DungeonStateStore State;
        internal readonly DungeonContentService Service;
        internal readonly Persistence Persistence = new();
        internal int Applied, Diagnosed;
        internal bool ThrowNative;
        internal Fixture()
        {
            State = new(Hub, Persistence);
            var session = Hub.Begin(SessionOrigin.SaveLoad, "save"); Hub.PlayerReady(session);
            Persistence.Provider.Restore(Hub.CurrentSession!, null);
            Service = new(Hub, new(_ => true, _ => true, _ => true), State,
                new((_, _) => DungeonContentStatus.Attached, (_, _) => { }, (_, _, _) => DungeonContentStatus.ChoiceApplied,
                    (_, _, _) => { Applied++; if (ThrowNative) throw new InvalidOperationException("native failure"); }), (_, _) => Diagnosed++);
        }
        internal BoardingHandle Target => new(Hub.CurrentSession!.Id, Guid.NewGuid());
        public void Dispose() { Service.Dispose(); State.Dispose(); Hub.Dispose(); }
    }
    private static DungeonDefinition Definition(int version = 1) => new(version, "Dungeon", new DungeonLayout(new[]
    {
        new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, new[] { "room" }),
        new DungeonCompartmentDefinition("room", CompartmentType.Corridor, new[] { "entry" })
    }), events: new[] { new DungeonEventDefinition("event", "room", "Choose", new[] { new DungeonChoiceDefinition("choice", "Choose") }) });
    [Fact]
    public void CargoRecoveryExampleCreatesIndependentSavedAuthoredContent()
    {
        using var f = new Fixture(); using var example = new AuthoredDungeon.CargoRecovery(f.Service, "recovery", "native-item");
        var first = example.Attach(f.Target).OccurrenceId!.Value;
        var second = example.Attach(f.Target).OccurrenceId!.Value;
        Assert.NotEqual(first, second);
        Assert.Equal(DungeonContentStatus.ChoiceApplied, example.Recover(first).Status);
        Assert.Equal(DungeonContentStatus.ChoiceApplied, example.Leave(second).Status);
        var restored = DungeonStateCodec.Decode(f.Persistence.Provider.Capture());
        Assert.Equal(2, restored.Count);
        Assert.All(restored, occurrence =>
        {
            Assert.Equal(3, occurrence.Definition.Layout.Compartments.Count);
            Assert.False(occurrence.Definition.AllowHazards);
            Assert.False(occurrence.Definition.AllowScheduledReinforcements);
        });
    }
    [Fact]
    public void ProvidersCannotReadOrChooseEachOthersIndependentOccurrences()
    {
        using var f = new Fixture(); using var a = f.Service.AcquireProvider("a"); using var b = f.Service.AcquireProvider("b");
        using var ar = a.Register("shared", Definition()); using var br = b.Register("shared", Definition());
        var first = a.Attach("shared", f.Target); var second = a.Attach("shared", f.Target); var third = b.Attach("shared", f.Target);
        Assert.NotEqual(first.OccurrenceId, second.OccurrenceId); Assert.Equal(2, a.GetOccurrences().Count); Assert.Single(b.GetOccurrences());
        Assert.Equal(DungeonContentStatus.MissingDefinition, b.Choose(first.OccurrenceId!.Value, "event", "choice").Status);
        Assert.Equal(DungeonContentStatus.ChoiceApplied, a.Choose(first.OccurrenceId.Value, "event", "choice").Status);
        Assert.Equal(DungeonContentStatus.AlreadyChosen, a.Choose(first.OccurrenceId.Value, "event", "choice").Status); Assert.Equal(1, f.Applied);
    }
    [Fact]
    public void MissingAndChangedDefinitionsDoNotSilentlyAdoptSavedOccurrences()
    {
        using var f = new Fixture(); using var provider = f.Service.AcquireProvider("a"); var registration = provider.Register("id", Definition());
        var id = provider.Attach("id", f.Target).OccurrenceId!.Value; registration.Dispose();
        Assert.Equal(DungeonContentStatus.MissingDefinition, provider.Choose(id, "event", "choice").Status);
        using var changed = provider.Register("id", Definition(2));
        Assert.Equal(DungeonContentStatus.VersionMismatch, provider.Choose(id, "event", "choice").Status);
        Assert.Equal(1, provider.GetOccurrences()[0].DefinitionVersion); Assert.Equal(0, f.Applied);
    }
    [Fact]
    public void CallbackFailureIsIsolatedButNativeFailurePropagatesWithoutRetry()
    {
        using var f = new Fixture(); using var provider = f.Service.AcquireProvider("a");
        var registration = provider.Register("id", Definition(), _ => throw new Exception("provider failure"));
        var id = provider.Attach("id", f.Target).OccurrenceId!.Value;
        Assert.Equal(DungeonContentStatus.Vetoed, provider.Choose(id, "event", "choice").Status); Assert.Equal(1, f.Diagnosed); Assert.Equal(0, f.Applied);
        registration.Dispose(); using var replacement = provider.Register("id", Definition()); f.ThrowNative = true;
        Assert.Throws<InvalidOperationException>(() => provider.Choose(id, "event", "choice"));
        Assert.Equal(DungeonContentStatus.AlreadyChosen, provider.Choose(id, "event", "choice").Status); Assert.Equal(1, f.Applied);
    }
    [Fact]
    public void CallbackCannotReenterAndDisposalRevokesItsPendingChoice()
    {
        using var f = new Fixture(); var provider = f.Service.AcquireProvider("a"); Guid id = default;
        using var registration = provider.Register("id", Definition(), _ =>
        {
            Assert.Equal(DungeonContentStatus.Unavailable, provider.Choose(id, "event", "choice").Status);
            provider.Dispose(); return true;
        });
        id = provider.Attach("id", f.Target).OccurrenceId!.Value;
        Assert.Equal(DungeonContentStatus.Unavailable, provider.Choose(id, "event", "choice").Status); Assert.Equal(0, f.Applied);
    }
}
