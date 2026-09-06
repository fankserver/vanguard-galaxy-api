using System;
using System.Collections.Generic;
using System.IO;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class MissionIdentityPersistenceTests
{
    private sealed class Persistence : IPersistenceApi, IPersistenceRegistration
    {
        internal PersistenceProvider Provider = null!;
        internal bool Disposed;
        public IPersistenceRegistration Register(PersistenceProvider provider) { Provider = provider; return this; }
        public bool MutationAllowed => !Disposed;
        public string Status => Disposed ? "inactive" : "ready";
        public void Dispose() => Disposed = true;
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestoreDeliveryDistinguishesMissingFromUnavailable(bool delivered)
    {
        var persistence = new Persistence(); using var owner = new MissionIdentityPersistence(persistence, () => true);
        using var events = new MissionTransitions((_, _) => { });
        var id = Guid.NewGuid(); events.Reset(id); var mission = new object();
        if (delivered) persistence.Provider.Restore(new SessionSnapshot(id, SessionPhase.PlayerReady, SessionOrigin.SaveLoad, "test.save"), null);
        owner.Seed(events, id, new[] { mission }, new[] { new string('a', 64) });
        var received = new List<MissionTransition>(); events.Subscribe("test", received.Add);
        using (var observation = events.Begin()) events.Record(observation, mission, MissionTransitionKind.Restored, new MissionFacts(false, true), null, "name", Array.Empty<string>());
        Assert.Equal(delivered ? MissionIdentityEvidence.MissingOrAmbiguous : MissionIdentityEvidence.Unavailable, Assert.Single(received).Mission.IdentityEvidence);
    }
    [Fact]
    public void CapturedIdentityRestoresOnlyInItsDeliveredSession()
    {
        var persistence = new Persistence(); using var owner = new MissionIdentityPersistence(persistence, () => true);
        var mission = new object(); var json = new object(); var guid = Guid.NewGuid(); var fingerprint = new string('a', 64);
        var capture = owner.Snapshots.Begin(new[] { mission }, _ => guid, 1);
        Assert.True(owner.Snapshots.Complete(capture, json, new[] { mission }, new[] { fingerprint }, 1));
        byte[] bytes; using (owner.Snapshots.BeginStore(json)) bytes = persistence.Provider.Capture();
        Assert.True(persistence.Provider.Validate(bytes));
        var id = Guid.NewGuid(); persistence.Provider.Restore(new SessionSnapshot(id, SessionPhase.PlayerReady, SessionOrigin.SaveLoad, "test.save"), bytes);
        using var events = new MissionTransitions((_, _) => { }); events.Reset(id); var loaded = new object();
        owner.Seed(events, id, new[] { loaded }, new[] { fingerprint }); Assert.Equal(guid, events.SnapshotIdentity(loaded));
        owner.Reset(); events.Reset(Guid.NewGuid());
        Assert.Throws<InvalidDataException>(() => persistence.Provider.Capture());
        owner.Dispose(); Assert.True(persistence.Disposed);
    }
    [Fact]
    public void FaultedObserverCannotCaptureAnOuterStorePayload()
    {
        bool available = true; string? refusal = null; var persistence = new Persistence(); using var owner = new MissionIdentityPersistence(persistence, () => available, detail => refusal = detail);
        var mission = new object(); var json = new object();
        var capture = owner.Snapshots.Begin(new[] { mission }, _ => Guid.NewGuid(), 1);
        owner.Snapshots.Complete(capture, json, new[] { mission }, new[] { new string('a', 64) }, 1);
        using var store = owner.Snapshots.BeginStore(json); available = false;
        Assert.Throws<InvalidDataException>(() => persistence.Provider.Capture());
        Assert.Contains("all owners paused", refusal);
        Assert.False(persistence.Provider.Validate(new byte[] { 1, 2 }));
    }
}
