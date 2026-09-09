using System;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Runtime;
using VGModAPI.Patches;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;
namespace VGModAPI.Tests;
[Collection("Dungeon crew resume")]
public sealed class DungeonDonorHookTests
{
    [Theory]
    [InlineData("unchanged")]
    [InlineData("switched")]
    [InlineData("stale")]
    [InlineData("exception")]
    public void ProductionAbortFinalizerRetiresOnlyCapturedCurrentSwitchedAction(string transition)
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new DungeonPodPersistenceTests.Persistence(); using var state = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var id = Guid.NewGuid(); var other = Guid.NewGuid();
        foreach (var operation in new[] { id, other }) state.TrackOperation(new(operation, Guid.NewGuid(), null, "ship", "Ship", "Approach", "", "", DungeonTerminalProgress.NotStarted, false, donors: new[] { new DungeonDonorApproachState("donor", new Dictionary<string, int> { ["Marine"] = 2 }) }));
        var native = new DungeonLayoutBuilderTests.Native(); var actions = new NativeObject(); actions.Fields["donorDispatched"] = false; actions.Fields["donorTarget"] = null;
        var ship = new NativeObject(); ship.Fields["donorActions"] = actions;
        var hooks = new DungeonDonorRecoveryHooks(state, native, target => target != null, _ => throw new InvalidOperationException("Missing target must take abort path"), error => throw error);
        hooks.Capture(actions, id, "donor", ship); DungeonDonorPatches.Hooks = hooks;
        try
        {
            var snapshot = persistence.Provider.Capture();
            Assert.True(DungeonDonorPatches.DonorUpdate.Prefix(actions, out var lease)); Assert.NotNull(lease);
            Assert.Throws<InvalidOperationException>(() => persistence.Provider.Capture());
            if (transition != "unchanged") ship.Fields["donorActions"] = new object();
            if (transition == "stale") persistence.Provider.Restore(hub.CurrentSession!, snapshot);
            var error = transition == "exception" ? new InvalidOperationException("native abort") : null;
            Assert.Same(error, DungeonDonorPatches.DonorUpdate.Finalizer(actions, error, lease));
            Assert.Equal(transition == "switched" ? 0 : 1, state.Operation(id)!.Donors.Count);
            Assert.Single(state.Operation(other)!.Donors);
            if (error != null) Assert.Throws<InvalidOperationException>(() => persistence.Provider.Capture());
            else { var saved = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, saved); Assert.Equal(transition == "switched" ? 0 : 1, state.Operation(id)!.Donors.Count); }
        }
        finally { DungeonDonorPatches.Hooks = null; }
    }
}
