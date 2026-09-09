using System;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;
public sealed class WorldEmptyCombatEnvironmentTests
{
    private class Mission { public string? storyId = null; }
    private sealed class GuildMission : Mission { }
    [Fact]
    public void OnlyNativeExcludedMissionShapesPassTheRestrictedProfile()
    {
        var field = typeof(Mission).GetField("storyId")!;
        var excluded = new[] { typeof(GuildMission) };
        Assert.False(WorldEmptyCombatEnvironment.MissionExcluded(new Mission(), field, excluded));
        Assert.True(WorldEmptyCombatEnvironment.MissionExcluded(new Mission { storyId = "owned-story" }, field, excluded));
        Assert.True(WorldEmptyCombatEnvironment.MissionExcluded(new Mission { storyId = "" }, field, excluded));
        Assert.True(WorldEmptyCombatEnvironment.MissionExcluded(new GuildMission(), field, excluded));
    }
}
