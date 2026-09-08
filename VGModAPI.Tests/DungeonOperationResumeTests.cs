using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonOperationResumeTests
{
    [Fact]
    public void TerminalAttemptBlocksSaveAndCannotBeReplayedAfterReload()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new DungeonPodPersistenceTests.Persistence(); using var state = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var id = Guid.NewGuid(); DungeonPodPersistenceTests.TrackOperation(state, id);
        using (var attempt = state.BeginTerminal(id))
        {
            Assert.NotNull(attempt); Assert.Throws<InvalidOperationException>(() => state.EnsureSerializationAllowed());
            Assert.Throws<InvalidOperationException>(() => persistence.Provider.Capture()); Assert.Null(state.BeginTerminal(id));
        }
        var interrupted = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, interrupted);
        Assert.Equal(DungeonTerminalProgress.Attempted, state.Operation(id)!.TerminalProgress); Assert.Null(state.BeginTerminal(id));
        persistence.Provider.Restore(hub.CurrentSession!, null); DungeonPodPersistenceTests.TrackOperation(state, id);
        using (var attempt = state.BeginTerminal(id)) attempt!.Completed();
        var completed = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, completed);
        Assert.Equal(DungeonTerminalProgress.Completed, state.Operation(id)!.TerminalProgress); Assert.Null(state.BeginTerminal(id));
    }
    [Fact]
    public void SeparateOperationsOnSameContentKeepIndependentTerminalGuardsAcrossRollback()
    {
        var content = Guid.NewGuid(); var location = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        DungeonOperationResumeState State(Guid id, DungeonTerminalProgress progress, string mission = "mission") => new(id, location, content, "ship", "HostileShip", "Extraction", "Victory", mission, progress, false);
        var ledger = new DungeonOperationRecoveryLedger(); ledger.Track(State(first, DungeonTerminalProgress.NotStarted)); ledger.Track(State(second, DungeonTerminalProgress.NotStarted));
        var before = ledger.Capture(); ledger.Track(State(first, DungeonTerminalProgress.Attempted));
        var attempted = ledger.Capture(); ledger.Restore(attempted);
        Assert.False(ledger.Get(first)!.MayStartTerminalEffects); Assert.True(ledger.Get(second)!.MayStartTerminalEffects);
        Assert.Throws<InvalidOperationException>(() => ledger.Track(State(first, DungeonTerminalProgress.Attempted, "")));
        Assert.Throws<InvalidOperationException>(() => ledger.Track(State(second, DungeonTerminalProgress.Completed)));
        ledger.Restore(before); Assert.True(ledger.Get(first)!.MayStartTerminalEffects);
        ledger.Restore(null); Assert.Null(ledger.Get(first));
    }
}
