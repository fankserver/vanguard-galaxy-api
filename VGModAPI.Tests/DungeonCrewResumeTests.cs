using System;
using System.IO;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;

namespace VGModAPI.Tests;

public sealed class DungeonCrewResumeTests
{
    [Fact]
    public void WithdrawalHealingAndDazeRoundTripWithoutReinitializingNativeCrew()
    {
        var native = new DungeonLayoutBuilderTests.Native(); var adapter = new DungeonCrewResumeAdapter(native);
        var crew = new NativeObject(); crew.Fields["resumeDirectiveTarget"] = 2; crew.Fields["resumeFleeDelay"] = 1.25f;
        crew.Fields["resumeWithdrawing"] = true; crew.Fields["resumeRetreatOrigin"] = 3;
        crew.Fields["resumeRecoveryProgress"] = 0.4f; crew.Fields["resumeDazedTime"] = 12f;
        var encoded = DungeonCrewResumeCodec.Encode(adapter.Capture(crew)); var restored = DungeonCrewResumeCodec.Decode(encoded);
        var replacement = new NativeObject(); replacement.Fields["hp"] = 11;
        Assert.Throws<InvalidOperationException>(() => adapter.Restore(replacement, restored, 2)); Assert.Single(replacement.Fields);
        adapter.Restore(replacement, restored, 4);
        Assert.Equal(11, replacement.Fields["hp"]); Assert.Equal(encoded, DungeonCrewResumeCodec.Encode(adapter.Capture(replacement)));
        Assert.Throws<InvalidDataException>(() => DungeonCrewResumeCodec.Decode(new byte[23]));
        encoded[9] = 2; Assert.Throws<InvalidDataException>(() => DungeonCrewResumeCodec.Decode(encoded));
    }
    [Fact]
    public void EnclosingSimulationValidatesAllUnitsBeforeApplyingAnySupplement()
    {
        var adapter = new DungeonCrewResumeAdapter(new DungeonLayoutBuilderTests.Native()); var hydrator = new DungeonCrewResumeHydrator(adapter);
        var first = new NativeObject(); var second = new NativeObject();
        hydrator.Stage(first, new DungeonCrewResumeState(0, -1, false, -1, 0.25f, 0));
        hydrator.Stage(second, new DungeonCrewResumeState(3, -1, true, 1, 0, 1));
        Assert.False(hydrator.Apply(new[] { first, second }, 2)); Assert.Empty(first.Fields); Assert.Empty(second.Fields);
        Assert.True(hydrator.Apply(new[] { first, second }, 4));
        first.Fields["resumeRecoveryProgress"] = 0.75f;
        Assert.True(hydrator.Apply(new[] { first, second }, 4)); Assert.Equal(0.75f, first.Fields["resumeRecoveryProgress"]);
    }
    [Fact]
    public void MissingDirectiveAndFleeSentinelsRemainValidButNonfiniteTimersDoNot()
    {
        var saved = new DungeonCrewResumeState(-1, -1, false, -1, 0, 0);
        Assert.True(saved.Fits(1)); Assert.False(saved.Fits(0));
        Assert.Throws<ArgumentException>(() => new DungeonCrewResumeState(-1, float.NaN, false, -1, 0, 0));
        Assert.Throws<ArgumentException>(() => new DungeonCrewResumeState(-1, 0, false, -1, 0, float.PositiveInfinity));
    }
}
