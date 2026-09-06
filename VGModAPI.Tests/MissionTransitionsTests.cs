using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class MissionTransitionsTests
{
    private static void Record(MissionTransitions hub, MissionTransitions.Observation observation, object token, MissionTransitionKind kind, MissionFacts facts)
        => hub.Record(observation, token, kind, facts, "repeated-story", "Mission", new[] { "Mining", "category:Salvage" });

    [Fact]
    public void ReturnWithoutTransitionAndRewardIneligibilityEmitNothing()
    {
        using var hub = new MissionTransitions((_, _) => { }); hub.Reset(Guid.NewGuid());
        var events = new List<MissionTransition>(); using var sub = hub.Subscribe("test", events.Add);
        using (var observation = hub.Begin())
        {
            Record(hub, observation, new object(), MissionTransitionKind.Accepted, new(true, true));
            Record(hub, observation, new object(), MissionTransitionKind.Completed, new(true, true));
            Record(hub, observation, new object(), MissionTransitionKind.Completed, new(true, false));
        }
        Assert.Empty(events);
    }

    [Fact]
    public void NestedCompletionArchiveAndRemovalAreOrderedAndDeduplicated()
    {
        using var hub = new MissionTransitions((_, _) => { }); hub.Reset(Guid.NewGuid());
        var events = new List<MissionTransition>(); using var sub = hub.Subscribe("test", events.Add); var token = new object();
        using (var accept = hub.Begin()) Record(hub, accept, token, MissionTransitionKind.Accepted, new(false, true));
        using (var completion = hub.Begin())
        {
            using (var archive = hub.Begin())
            {
                Record(hub, archive, token, MissionTransitionKind.Archived, new(true, false, ArchiveBefore: 0, ArchiveAfter: 1));
                Record(hub, archive, token, MissionTransitionKind.Archived, new(true, false, ArchiveBefore: 0, ArchiveAfter: 1));
            }
            Record(hub, completion, token, MissionTransitionKind.Completed, new(true, false, RewardRemovalObserved: true));
            Assert.Single(events);
        }
        Assert.Equal(new[] { MissionTransitionKind.Accepted, MissionTransitionKind.Completed, MissionTransitionKind.Archived }, events.Select(e => e.Kind));
        Assert.Single(events.Select(e => e.Mission.InstanceId).Distinct());
        Assert.All(events, e => Assert.True(e.Mission.AcceptanceObserved));
    }

    [Fact]
    public void FailurePrecedesReplacementAndReusedObjectsStartNewOccurrences()
    {
        using var hub = new MissionTransitions((_, _) => { }); hub.Reset(Guid.NewGuid());
        var events = new List<MissionTransition>(); using var sub = hub.Subscribe("test", events.Add); var token = new object();
        using (var restored = hub.Begin()) Record(hub, restored, token, MissionTransitionKind.Restored, new(false, true));
        using (var failure = hub.Begin())
        {
            using (var remove = hub.Begin()) Record(hub, remove, token, MissionTransitionKind.Removed, new(true, false, AfterFailed: true));
            using (var replacement = hub.Begin()) Record(hub, replacement, new object(), MissionTransitionKind.Accepted, new(false, true));
            Record(hub, failure, token, MissionTransitionKind.Failed, new(true, false, AfterFailed: true));
        }
        Assert.Equal(new[] { MissionTransitionKind.Restored, MissionTransitionKind.Failed, MissionTransitionKind.Removed, MissionTransitionKind.Accepted }, events.Select(e => e.Kind));
        Assert.False(events[1].Mission.AcceptanceObserved);
        using (var again = hub.Begin()) Record(hub, again, token, MissionTransitionKind.Accepted, new(false, true));
        Assert.NotEqual(events[0].Mission.InstanceId, events.Last().Mission.InstanceId);
    }

    [Fact]
    public void NestedFailureDuringAcceptanceKeepsIdentityAndLogicalAcceptanceKnowledge()
    {
        using var hub = new MissionTransitions((_, _) => { }); hub.Reset(Guid.NewGuid());
        var events = new List<MissionTransition>(); using var sub = hub.Subscribe("test", events.Add); var token = new object();
        using (var accept = hub.Begin())
        {
            using (var failed = hub.Begin()) Record(hub, failed, token, MissionTransitionKind.Failed, new(true, false, AfterFailed: true));
            using (var removed = hub.Begin()) Record(hub, removed, token, MissionTransitionKind.Removed, new(true, false, AfterFailed: true));
            // Adapter supplies the witnessed intermediate insertion, not merely the outer return state.
            Record(hub, accept, token, MissionTransitionKind.Accepted, new(false, true));
        }
        Assert.Equal(MissionTransitionKind.Accepted, events[0].Kind);
        Assert.Single(events.Select(e => e.Mission.InstanceId).Distinct());
        Assert.All(events, e => Assert.True(e.Mission.AcceptanceObserved));
    }

    [Fact]
    public void ReplacementDiscardsPendingAndSubscribersCannotBreakDispatch()
    {
        int errors = 0; using var hub = new MissionTransitions((_, _) => errors++); hub.Reset(Guid.NewGuid());
        var events = new List<MissionTransition>(); using var bad = hub.Subscribe("bad", _ => throw new Exception("subscriber"));
        using var good = hub.Subscribe("good", events.Add);
        using (var stale = hub.Begin()) { Record(hub, stale, new object(), MissionTransitionKind.Accepted, new(false, true)); hub.Reset(Guid.NewGuid()); }
        Assert.Empty(events);
        using (var restored = hub.Begin()) Record(hub, restored, new object(), MissionTransitionKind.Restored, new(false, true));
        Assert.Single(events); Assert.Equal(1, errors); Assert.False(events[0].Mission.AcceptanceObserved);
    }

    [Fact]
    public void ReentrantResetAndDisposalStopOldBatchAndOffThreadUseFails()
    {
        using var hub = new MissionTransitions((_, _) => { }); hub.Reset(Guid.NewGuid());
        int delivered = 0;
        using var first = hub.Subscribe("replace", _ => { delivered++; hub.Reset(Guid.NewGuid()); });
        using var second = hub.Subscribe("late", _ => delivered++);
        using (var scope = hub.Begin())
        {
            Record(hub, scope, new object(), MissionTransitionKind.Restored, new(false, true));
            Record(hub, scope, new object(), MissionTransitionKind.Restored, new(false, true));
        }
        Assert.Equal(1, delivered);
        Exception? error = null;
        var thread = new System.Threading.Thread(() => { try { hub.Begin(); } catch (Exception caught) { error = caught; } });
        thread.Start(); thread.Join(); Assert.IsType<InvalidOperationException>(error);
        var stale = hub.Begin(); hub.Dispose(); stale.Dispose();
        Assert.Throws<ObjectDisposedException>(() => hub.Begin());
    }

    [Fact]
    public void RepeatedDefinitionsAndLoadEpochsNeverShareLiveIdentity()
    {
        using var hub = new MissionTransitions((_, _) => { }); hub.Reset(Guid.NewGuid());
        var events = new List<MissionTransition>(); using var sub = hub.Subscribe("test", events.Add); var token = new object();
        using (var scope = hub.Begin())
        {
            Record(hub, scope, token, MissionTransitionKind.Restored, new(false, true));
            Record(hub, scope, new object(), MissionTransitionKind.Restored, new(false, true));
        }
        hub.Reset(Guid.NewGuid());
        using (var scope = hub.Begin()) Record(hub, scope, token, MissionTransitionKind.Restored, new(false, true));
        Assert.Equal(3, events.Select(e => e.Mission.InstanceId).Distinct().Count());
        Assert.All(events, e => Assert.False(e.Mission.AcceptanceObserved));
    }

    [Fact]
    public void SnapshotCopiesTagsAndRejectsInvalidIdentity()
    {
        var tags = new[] { "Mining", "category:Salvage", "Mining" };
        var snapshot = new MissionSnapshot(Guid.NewGuid(), Guid.NewGuid(), "story", "name", tags, false); tags[0] = "changed";
        Assert.Equal(new[] { "Mining", "category:Salvage" }, snapshot.ObjectiveTags);
        Assert.Throws<ArgumentException>(() => new MissionSnapshot(Guid.Empty, Guid.NewGuid(), null, "name", tags, false));
    }
}
