using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;
namespace VGModAPI.Tests;
public sealed class DungeonInitialPodPlacementTests
{
    [Theory]
    [InlineData(0, 0, true, true, 4, 5, -90, true)]
    [InlineData(1, 2, false, false, 1, 2, 30, false)]
    [InlineData(2, 1, true, false, 4, 5, 30, false)]
    public void RestoredPhaseSelectsParentCoordinatesAndPendingMembership(int phase, int parent, bool localPosition, bool localRotation, float x, float y, float angle, bool pending)
    {
        var saved = new DungeonPodResumeState(Guid.NewGuid(), Guid.NewGuid(), (DungeonPodPhase)phase, true, false, false, new Dictionary<string, int>(), transport: new("pod", true, new Dictionary<string, int> { ["Marine"] = 2 }, new float[] { 1, 2, 30, 4, 5, 6, 7, 8, 9 }, "donor"));
        var restored = Assert.Single(DungeonPodResumeCodec.Decode(DungeonPodResumeCodec.Encode(new[] { saved })));
        var placement = new DungeonInitialPodPlacement(restored);
        Assert.Equal((DungeonPodParent)parent, placement.Parent); Assert.Equal(localPosition, placement.LocalPosition); Assert.Equal(localRotation, placement.LocalRotation);
        Assert.Equal(x, placement.X); Assert.Equal(y, placement.Y); Assert.Equal(angle, placement.Angle); Assert.Equal(pending, placement.PendingReinforcement);
        Assert.Equal((DungeonPodPhase)phase, restored.Phase); Assert.Equal(2, restored.Transport!.OutboundCrew["Marine"]);
    }
}
