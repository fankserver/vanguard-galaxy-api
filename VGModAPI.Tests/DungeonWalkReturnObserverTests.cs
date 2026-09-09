using System;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;
namespace VGModAPI.Tests;
public sealed class DungeonWalkReturnObserverTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExtractionReceiptIsIndependentOfCompletedRewardsAndCannotRetry(bool fullReceipt)
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new DungeonPodPersistenceTests.Persistence(); using var state = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var crew = new Dictionary<string, int> { ["Marine"] = 2 }; var id = Guid.NewGuid();
        Assert.True(state.TrackOperation(new(id, Guid.NewGuid(), null, "ship", "Station", "Extraction", "Victory", "", DungeonTerminalProgress.NotStarted, false, walkReturn: new(crew))));
        using (var terminal = state.BeginTerminal(id)) { Assert.NotNull(terminal); terminal!.Completed(); }
        Assert.True(state.Operation(id)!.MayResumeWalkExtraction);
        var native = new DungeonLayoutBuilderTests.Native(); var pods = new DungeonPodResumeAdapter(state, native);
        var recipient = new NativeObject(); recipient.Fields["resumeShipGuid"] = "wrong";
        var ship = new NativeObject(); ship.Fields["resumeShipData"] = recipient;
        var operation = new NativeObject(); operation.Fields["operationShip"] = ship; operation.Fields["simulation"] = new object(); operation.Fields["walkManifest"] = crew;
        var observer = new DungeonPodReturnObserver(state, pods, native, _ => new object(), _ => true, _ => id);
        Assert.False(observer.BeginWalk(operation, out _));
        recipient.Fields["resumeShipGuid"] = "ship";
        Assert.True(observer.BeginWalk(operation, out var scope));
        using (scope)
        {
            Assert.Throws<InvalidOperationException>(() => persistence.Provider.Capture());
            observer.CrewAdded(recipient, "Marine", 2, fullReceipt ? 0 : 1);
            scope!.Complete();
        }
        Assert.Equal(fullReceipt ? DungeonWalkReturnProgress.Delivered : DungeonWalkReturnProgress.Attempted, state.Operation(id)!.WalkReturn!.Progress);
        Assert.Equal(DungeonTerminalProgress.Completed, state.Operation(id)!.TerminalProgress);
        Assert.False(observer.BeginWalk(operation, out _));
        var snapshot = persistence.Provider.Capture(); persistence.Provider.Restore(hub.CurrentSession!, snapshot);
        Assert.False(state.Operation(id)!.MayResumeWalkExtraction); Assert.Null(state.BeginTerminal(id)); Assert.Null(state.BeginWalkReturn(id));
    }
}
