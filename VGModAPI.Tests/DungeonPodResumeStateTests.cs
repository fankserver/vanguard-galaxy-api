using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonPodResumeStateTests
{
    [Fact]
    public void ReturningPodRequiresActualCopiedManifestNotOutboundCrew()
    {
        var crew = new Dictionary<string, int> { ["Marine"] = 2 };
        var saved = new DungeonPodResumeState(Guid.NewGuid(), Guid.NewGuid(), DungeonPodPhase.Returning, true, true, false, crew);
        crew["Marine"] = 9;
        Assert.Equal(2, saved.ReturnCrew["Marine"]); Assert.True(saved.CanRecover); Assert.False(saved.ReturnDelivered);
        var unknown = new DungeonPodResumeState(Guid.NewGuid(), saved.OperationId, DungeonPodPhase.Returning, true, false, false, new Dictionary<string, int>());
        Assert.True(unknown.RequiresRecovery); Assert.False(unknown.CanRecover);
    }
    [Fact]
    public void ArrivalOrEmptyManifestAloneCannotAttestDelivery()
    {
        var arrived = new DungeonPodResumeState(Guid.NewGuid(), Guid.NewGuid(), DungeonPodPhase.Arrived, true, true, false, new Dictionary<string, int>());
        Assert.False(arrived.ReturnDelivered);
        Assert.Throws<ArgumentException>(() => new DungeonPodResumeState(Guid.NewGuid(), arrived.OperationId, DungeonPodPhase.Returning, true, true, true, new Dictionary<string, int>()));
    }
}
