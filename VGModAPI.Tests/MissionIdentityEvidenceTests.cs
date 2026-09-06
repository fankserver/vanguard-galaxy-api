using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class MissionIdentityEvidenceTests
{
    [Fact]
    public void MatchedIdentityDoesNotFabricateAcceptanceHistory()
    {
        using var events = new MissionTransitions((_, _) => { }); events.Reset(Guid.NewGuid());
        var received = new List<MissionTransition>(); events.Subscribe("test", received.Add);
        var mission = new object(); var id = Guid.NewGuid();
        events.SeedIdentity(mission, id, MissionIdentityEvidence.SavedSnapshotMatch);
        using (var observation = events.Begin()) events.Record(observation, mission, MissionTransitionKind.Restored, new MissionFacts(false, true), "definition", "name", Array.Empty<string>());
        var restored = Assert.Single(received).Mission;
        Assert.Equal(id, restored.InstanceId); Assert.Equal(MissionIdentityEvidence.SavedSnapshotMatch, restored.IdentityEvidence); Assert.False(restored.AcceptanceObserved);
    }
    [Theory]
    [InlineData(MissionIdentityEvidence.MissingOrAmbiguous)]
    [InlineData(MissionIdentityEvidence.Unavailable)]
    public void UnmatchedRestorationIsExplicitAndSessionLocal(MissionIdentityEvidence evidence)
    {
        using var events = new MissionTransitions((_, _) => { }); events.Reset(Guid.NewGuid());
        var received = new List<MissionTransition>(); events.Subscribe("test", received.Add); var mission = new object();
        events.SeedIdentity(mission, null, evidence);
        using (var observation = events.Begin()) events.Record(observation, mission, MissionTransitionKind.Restored, new MissionFacts(false, true), null, "name", Array.Empty<string>());
        Assert.Equal(evidence, Assert.Single(received).Mission.IdentityEvidence);
        Assert.Throws<InvalidOperationException>(() => events.SeedIdentity(mission, null, evidence));
    }
    [Fact]
    public void SnapshotOnlyOccurrenceDoesNotReuseIdOnLaterWitnessedAcceptance()
    {
        using var events = new MissionTransitions((_, _) => { }); events.Reset(Guid.NewGuid());
        var received = new List<MissionTransition>(); events.Subscribe("test", received.Add); var mission = new object();
        var original = events.SnapshotIdentity(mission);
        using (var observation = events.Begin()) events.Record(observation, mission, MissionTransitionKind.Accepted, new MissionFacts(false, true), null, "name", Array.Empty<string>());
        Assert.NotEqual(original, Assert.Single(received).Mission.InstanceId);
    }
    [Fact]
    public void FailedOnlyOccurrenceDoesNotReuseIdOnLaterAcceptance()
    {
        using var events = new MissionTransitions((_, _) => { }); events.Reset(Guid.NewGuid());
        var received = new List<MissionTransition>(); events.Subscribe("test", received.Add); var mission = new object();
        using (var failure = events.Begin()) events.Record(failure, mission, MissionTransitionKind.Failed, new MissionFacts(false, false, false, true), null, "name", Array.Empty<string>());
        using (var acceptance = events.Begin()) events.Record(acceptance, mission, MissionTransitionKind.Accepted, new MissionFacts(false, true), null, "name", Array.Empty<string>());
        Assert.NotEqual(received[0].Mission.InstanceId, received[1].Mission.InstanceId);
    }
}
