using System;
using System.Collections;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Runtime;
using VGModAPI.Patches;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;
namespace VGModAPI.Tests;
[Collection("Dungeon crew resume")]
public sealed class DungeonTerminalRecoveryHookTests
{
    [Theory]
    [InlineData("FriendlyVictory")]
    [InlineData("FriendlyExtracted")]
    [InlineData("ShipExploded")]
    public void TerminalAdmissionExtractionAndOverflowRemainSettledAcrossReload(string outcome)
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new DungeonPodPersistenceTests.Persistence(); using var state = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var id = Guid.NewGuid(); var location = Guid.NewGuid(); var phase = "Active"; var crew = new Dictionary<string, int> { ["Marine"] = 2 };
        state.TrackOperation(new(id, location, null, "ship", "Station", phase, outcome, "", DungeonTerminalProgress.NotStarted, false));
        var native = new DungeonLayoutBuilderTests.Native(); var pods = new DungeonPodResumeAdapter(state, native);
        var recipient = new NativeObject(); recipient.Fields["resumeShipGuid"] = "ship"; var ship = new NativeObject(); ship.Fields["resumeShipData"] = recipient;
        var operation = new NativeObject(); operation.Fields["operationShip"] = ship; operation.Fields["simulation"] = new object(); operation.Fields["walkManifest"] = crew;
        var origin = new object(); var observer = new DungeonPodReturnObserver(state, pods, native, _ => origin, _ => true, _ => id);
        bool Observe(object unused)
        {
            var saved = state.Operation(id)!;
            return state.TrackOperation(new(id, location, null, "ship", "Station", phase, outcome, "", saved.TerminalProgress, false, walkReturn: saved.WalkReturn ?? (phase == "Extraction" ? new DungeonWalkReturnState(crew) : null)));
        }
        DungeonTerminalRecoveryPatches.Hooks = new(state, observer, Observe, _ => id);
        var terminalEffects = 0; var captures = 0;
        void Terminal()
        {
            if (!DungeonTerminalRecoveryPatches.Terminal.Prefix(operation, out var scope)) return;
            try { terminalEffects++; if (outcome == "FriendlyVictory") captures++; phase = "Extraction"; DungeonTerminalRecoveryPatches.Terminal.Postfix(scope); }
            finally { DungeonTerminalRecoveryPatches.Terminal.Finalizer(null, scope); }
        }
        try
        {
            Terminal(); Assert.True(Observe(operation));
            var saved = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, saved); Terminal();
            Assert.Equal(1, terminalEffects); Assert.Equal(outcome == "FriendlyVictory" ? 1 : 0, captures);
            Assert.True(DungeonTerminalRecoveryPatches.WalkComplete.Prefix(operation, out var walk));
            observer.CrewAdded(recipient, "Marine", 2, 1);
            using (var overflow = observer.BeginOverflow(origin, "Marine", 1))
            {
                var poi = new NativeObject(); var list = new ArrayList(); poi.Fields["persistables"] = list; var data = new Source.Data.Persistable.CrewPodData();
                observer.AddingPersistable(poi, data); list.Add(data); observer.AddedPersistable(poi, data); overflow.Complete();
            }
            DungeonTerminalRecoveryPatches.WalkComplete.Postfix(walk); DungeonTerminalRecoveryPatches.WalkComplete.Finalizer(null, walk);
            saved = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, saved);
            Assert.Equal(DungeonWalkReturnProgress.Delivered, state.Operation(id)!.WalkReturn!.Progress);
            Assert.False(DungeonTerminalRecoveryPatches.WalkComplete.Prefix(operation, out _)); Terminal(); Assert.Equal(1, terminalEffects);
        }
        finally { DungeonTerminalRecoveryPatches.Hooks = null; }
    }
}
