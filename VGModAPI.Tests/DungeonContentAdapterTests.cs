using System;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;

namespace VGModAPI.Tests;

public sealed class DungeonContentAdapterTests
{
    private sealed class Persistence : TestSaveDataService
    {
        internal PersistenceProvider Provider = null!;
        public bool MutationAllowed => StateReady;
        public bool StateReady { get; set; } = true;
        public string Status => "test";
        public override bool CanRead => StateReady;
        public override bool CanMutate => StateReady && MutationAllowed;
        public override SaveDataRegistrationResult Register(PersistenceProvider provider) { Provider = provider; return new(SaveDataRegistrationStatus.Registered, this); }
        public override void Dispose() { }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly Persistence Persistence = new();
        internal readonly DungeonStateStore Store;
        internal readonly DungeonContentAdapter Adapter;
        internal readonly NativeObject Simulation = new(), Location = new(), Operation = new();
        internal readonly Guid Id = Guid.NewGuid();
        internal Fixture(bool retained)
        {
            Store = new(Hub, Persistence); var session = Hub.Begin(SessionOrigin.SaveLoad, "slot"); Hub.PlayerReady(session);
            Persistence.Provider.Restore(Hub.CurrentSession!, null);
            Adapter = new(Hub, null!, Store, new DungeonLayoutBuilderTests.Native(), typeof(NativeObject), typeof(NativeObject), typeof(NativeObject), new[] { "Gold" });
            var data = new NativeObject(); data.Fields["authoredSavedSimulation"] = Simulation;
            Location.Fields["authoredLocationData"] = data;
            Operation.Fields["location"] = Location; Operation.Fields["simulation"] = Simulation;
            Simulation.Fields["authoredFaction"] = "Red"; Simulation.Fields["authoredProfile"] = "Red"; Simulation.Fields["authoredNoScuttle"] = true;
            Adapter.RestoreMarker(Location, Id);
            if (retained) Store.Add(new(Id, new("mod", "id"), new DungeonDefinition(1, "Dungeon", new DungeonLayout(new[]
            {
                new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, new[] { "room" }),
                new DungeonCompartmentDefinition("room", CompartmentType.Corridor, new[] { "entry" })
            }), "Gold", allowHazards: false, allowScheduledReinforcements: false)));
        }
        public void Dispose() { Adapter.Dispose(); Store.Dispose(); Hub.Dispose(); }
    }
    [Theory]
    [InlineData("Red")]
    [InlineData("Gold")]
    public void FreshEntryAndRestoredNativeOverwriteAreRepairedWithoutChangingLayout(string nativeFaction)
    {
        using var f = new Fixture(true);
        f.Simulation.Fields["authoredFaction"] = nativeFaction;
        Assert.True(f.Adapter.GuardOperation(f.Operation, true)); Assert.Equal("Gold", f.Simulation.Fields["authoredFaction"]);
        Assert.Equal("protected:Gold", f.Simulation.Fields["authoredProfile"]);
        // The native restored-operation constructor assigns the host faction again.
        f.Simulation.Fields["authoredFaction"] = nativeFaction; f.Simulation.Fields["authoredProfile"] = nativeFaction;
        Assert.True(f.Adapter.GuardOperation(f.Operation, true)); Assert.Equal("Gold", f.Simulation.Fields["authoredFaction"]);
        Assert.Equal("protected:Gold", f.Simulation.Fields["authoredProfile"]);
        Assert.False(f.Adapter.AllowEffect(f.Simulation, true)); Assert.False(f.Adapter.AllowEffect(f.Simulation, false));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MarkedRestoredSimulationCannotFallBackWhenStateIsMissingOrUnavailable(bool retained)
    {
        using var f = new Fixture(retained);
        if (retained) f.Persistence.StateReady = false;
        Assert.False(f.Adapter.GuardOperation(f.Operation));
        Assert.False(f.Adapter.AllowEffect(f.Simulation, true)); Assert.False(f.Adapter.AllowEffect(f.Simulation, false));
        Assert.True(f.Adapter.AllowEffect(new NativeObject(), true));
        var vanillaLocation = new NativeObject(); var vanillaOperation = new NativeObject(); vanillaOperation.Fields["location"] = vanillaLocation;
        Assert.True(f.Adapter.GuardOperation(vanillaOperation));
    }
}
