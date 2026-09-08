using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonPodResumeCodecTests
{
    [Fact]
    public void TransportSurvivesWithoutNativeLocationAndDoesNotAliasPoseOrCrew()
    {
        var crew = new Dictionary<string, int> { ["Marine"] = 4 }; var pose = new float[] { 10, 20, 30, 1, 2, 40, 50, 3, 4 };
        var transport = new DungeonPodTransport("native-pod", true, crew, pose);
        var state = new DungeonPodResumeState(Guid.NewGuid(), Guid.NewGuid(), DungeonPodPhase.Returning, true, true, false,
            new Dictionary<string, int> { ["Marine"] = 2 }, parentShipId: "recipient", transport: transport);
        crew["Marine"] = 9; pose[0] = 999;
        var decoded = DungeonPodResumeCodec.Decode(DungeonPodResumeCodec.Encode(new[] { state }))[0];
        Assert.Equal("native-pod", decoded.Transport!.NativePodId); Assert.True(decoded.Transport.PendingReinforcement);
        Assert.Equal(10, decoded.Transport.Pose[0]); Assert.Equal(4, decoded.Transport.OutboundCrew["Marine"]);
        Assert.Equal(2, decoded.ReturnCrew["Marine"]);
    }
    [Fact]
    public void EveryNativePodPhaseAndKnownEmptyReturnManifestRoundTrips()
    {
        var occurrence = Guid.NewGuid();
        var states = Enum.GetValues<DungeonPodPhase>().Select(phase => new DungeonPodResumeState(Guid.NewGuid(), occurrence, phase, true,
            phase is DungeonPodPhase.Returning or DungeonPodPhase.Arrived, phase == DungeonPodPhase.Arrived, new Dictionary<string, int>())).ToArray();
        var bytes = DungeonPodResumeCodec.Encode(states); var restored = DungeonPodResumeCodec.Decode(bytes);
        Assert.Equal(5, restored.Count); Assert.Equal(bytes, DungeonPodResumeCodec.Encode(restored));
        Assert.True(restored.Single(p => p.Phase == DungeonPodPhase.Returning).CanRecover);
        Assert.True(restored.Single(p => p.Phase == DungeonPodPhase.Arrived).ReturnDelivered);
    }
    [Fact]
    public void SeparateOccurrencesAndCorruptionNeverInventCrew()
    {
        var state = new DungeonPodResumeState(Guid.NewGuid(), Guid.NewGuid(), DungeonPodPhase.Returning, true, true, false, new Dictionary<string, int> { ["Marine"] = 3 });
        var bytes = DungeonPodResumeCodec.Encode(new[] { state });
        Assert.Equal(3, DungeonPodResumeCodec.Decode(bytes)[0].ReturnCrew["Marine"]);
        Assert.False(DungeonPodResumeCodec.Validate(bytes.Take(bytes.Length - 1).ToArray()));
        Assert.False(DungeonPodResumeCodec.Validate(bytes.Concat(new byte[] { 0 }).ToArray()));
        bytes[0] = 99; Assert.False(DungeonPodResumeCodec.Validate(bytes));
    }
}
