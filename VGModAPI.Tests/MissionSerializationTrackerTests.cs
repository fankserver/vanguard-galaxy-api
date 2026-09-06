using System;
using System.IO;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class MissionSerializationTrackerTests
{
    private static readonly string Fingerprint = new('a', 64);
    private static void Register(MissionSerializationTracker tracker, object json, object mission, Guid id, long revision)
    {
        var capture = tracker.Begin(new[] { mission }, _ => id, revision);
        Assert.True(tracker.Complete(capture, json, new[] { mission }, new[] { Fingerprint }, revision));
    }
    [Fact]
    public void AmbiguousRuntimeIdsDoNotChangePersistedBytesAcrossLoads()
    {
        var tracker = new MissionSerializationTracker(); var json = new object(); var missions = new[] { new object(), new object() };
        var capture = tracker.Begin(missions, _ => Guid.NewGuid(), 1);
        tracker.Complete(capture, json, missions, new[] { Fingerprint, Fingerprint }, 1);
        byte[] first; using (tracker.BeginStore(json, () => new[] { Fingerprint, Fingerprint })) first = tracker.CaptureForStore();
        Assert.Empty(MissionIdentitySnapshot.Decode(first));
        tracker.Reset(); capture = tracker.Begin(missions, _ => Guid.NewGuid(), 2);
        tracker.Complete(capture, json, missions, new[] { Fingerprint, Fingerprint }, 2);
        using (tracker.BeginStore(json, () => new[] { Fingerprint, Fingerprint })) Assert.Equal(first, tracker.CaptureForStore());
    }
    [Fact]
    public void MutationOfKnownJsonContentsCannotReuseOldAssociation()
    {
        var tracker = new MissionSerializationTracker(); var json = new object(); Register(tracker, json, new object(), Guid.NewGuid(), 1);
        using (tracker.BeginStore(json, () => new[] { new string('b', 64) })) Assert.Throws<InvalidDataException>(() => tracker.CaptureForStore());
        using (tracker.BeginStore(json, () => new[] { Fingerprint })) Assert.NotEmpty(tracker.CaptureForStore());
    }
    [Fact]
    public void StoreUsesItsSerializedObjectNotLaterLiveState()
    {
        var tracker = new MissionSerializationTracker(); var oldJson = new object(); var newJson = new object();
        var oldId = Guid.NewGuid(); var newId = Guid.NewGuid();
        Register(tracker, oldJson, new object(), oldId, 1); Register(tracker, newJson, new object(), newId, 2);
        using var outer = tracker.BeginStore(oldJson);
        Assert.Equal(oldId, Assert.Single(MissionIdentitySnapshot.Decode(tracker.CaptureForStore())).InstanceId);
        using (tracker.BeginStore(newJson))
            Assert.Equal(newId, Assert.Single(MissionIdentitySnapshot.Decode(tracker.CaptureForStore())).InstanceId);
        Assert.Equal(oldId, Assert.Single(MissionIdentitySnapshot.Decode(tracker.CaptureForStore())).InstanceId);
    }
    [Fact]
    public void UnknownNestedSnapshotDoesNotFallBackToOuterOrLatest()
    {
        var tracker = new MissionSerializationTracker(); var json = new object(); Register(tracker, json, new object(), Guid.NewGuid(), 1);
        using var outer = tracker.BeginStore(json);
        using (tracker.BeginStore(new object())) Assert.Throws<InvalidDataException>(() => tracker.CaptureForStore());
        Assert.NotEmpty(tracker.CaptureForStore());
    }
    [Fact]
    public void MembershipOrObservedMutationInvalidatesAssociation()
    {
        var tracker = new MissionSerializationTracker(); var json = new object(); var mission = new object();
        Register(tracker, json, mission, Guid.NewGuid(), 1);
        var capture = tracker.Begin(new[] { mission }, _ => Guid.NewGuid(), 1);
        Assert.False(tracker.Complete(capture, json, new[] { new object() }, new[] { Fingerprint }, 1));
        using (tracker.BeginStore(json)) Assert.Throws<InvalidDataException>(() => tracker.CaptureForStore());
        Assert.False(tracker.Complete(capture, json, new[] { mission }, new[] { Fingerprint }, 2));
    }
    [Fact]
    public void ResetRejectsStaleCaptureAndFinalizers()
    {
        var tracker = new MissionSerializationTracker(); var mission = new object(); var json = new object();
        var capture = tracker.Begin(new[] { mission }, _ => Guid.NewGuid(), 1); var old = tracker.BeginStore(null);
        tracker.Reset(); Register(tracker, json, mission, Guid.NewGuid(), 2);
        using var current = tracker.BeginStore(json);
        Assert.False(tracker.Complete(capture, json, new[] { mission }, new[] { Fingerprint }, 1));
        old.Dispose(); Assert.NotEmpty(tracker.CaptureForStore());
    }
    [Fact]
    public void OuterFinalizerUnwindsChildrenAndCapturedBytesAreCopies()
    {
        var tracker = new MissionSerializationTracker(); var json = new object(); Register(tracker, json, new object(), Guid.NewGuid(), 1);
        var outer = tracker.BeginStore(json); var inner = tracker.BeginStore(json);
        var bytes = tracker.CaptureForStore(); bytes[0] = 0;
        Assert.NotEqual(bytes, tracker.CaptureForStore());
        outer.Dispose(); inner.Dispose(); Assert.Throws<InvalidDataException>(() => tracker.CaptureForStore());
    }
}
