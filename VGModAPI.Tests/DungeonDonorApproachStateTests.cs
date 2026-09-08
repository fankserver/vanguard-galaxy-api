using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonDonorApproachStateTests
{
    [Fact]
    public void ReservationCopiesCrewWithoutDerivingItFromTheRemainingRoster()
    {
        var crew = new Dictionary<string, int> { ["Marine"] = 4 };
        var saved = new DungeonDonorApproachState("donor", crew); crew.Clear();
        Assert.Equal(4, saved.Crew["Marine"]); Assert.Equal("donor", saved.ShipId);
        Assert.Throws<ArgumentException>(() => new DungeonDonorApproachState("donor", crew));
    }
    [Fact]
    public void OperationEnvelopeRetainsReservationsAndRejectsTruncation()
    {
        var donor = new DungeonDonorApproachState("donor", new Dictionary<string, int> { ["Marine"] = 4 });
        var operation = new DungeonOperationResumeState(Guid.NewGuid(), Guid.NewGuid(), null, "player", "Ship", "Active", "", "", DungeonTerminalProgress.NotStarted, false, donors: new[] { donor });
        var bytes = DungeonOperationResumeCodec.Encode(new[] { operation });
        var restored = Assert.Single(DungeonOperationResumeCodec.Decode(bytes));
        Assert.Equal(4, Assert.Single(restored.Donors).Crew["Marine"]);
        Assert.ThrowsAny<Exception>(() => DungeonOperationResumeCodec.Decode(bytes[..^1]));
        Assert.Throws<ArgumentException>(() => new DungeonOperationResumeState(Guid.NewGuid(), Guid.NewGuid(), null, "player", "Ship", "Active", "", "", DungeonTerminalProgress.NotStarted, false, donors: new[] { donor, donor }));
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10001)]
    public void InvalidReservedCountsAreRejected(int count) => Assert.Throws<ArgumentException>(() =>
        new DungeonDonorApproachState("donor", new Dictionary<string, int> { ["Marine"] = count }));
}
