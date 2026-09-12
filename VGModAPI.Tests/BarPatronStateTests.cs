using System;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarPatronStateTests
{
    [Fact]
    public void SameLocalPatronNameIsIndependentAcrossProviders()
    {
        Assert.NotEqual(new BarPatronId("campaign", "contact"), new BarPatronId("jobs", "contact"));
        var state = new BarPatronState(new BarPatronId("campaign", "contact"), "CustomAct3RickoStation", "Contact", "Description", "seed");
        Assert.Equal("CustomAct3RickoStation", state.Station);
        Assert.Null(state.Mission);
        Assert.Null(state.Mission);
    }

    /// <summary>
    /// Definition and run are independent facts: a patron offers a definition, and separately may be
    /// bound to a run of it. "Offers X, not yet bound to a run" is a real state and must be storable.
    /// </summary>
    [Fact]
    public void MissionDefinitionAndBoundRunAreIndependentAndOwnerScoped()
    {
        var id = new BarPatronId("campaign", "contact");
        var mission = new StoryMissionDefinitionId("campaign", "mission-x");
        var missionId = Guid.NewGuid();
        var state = new BarPatronState(id, "station", "Contact", "Description", "seed", mission, missionId);
        Assert.Equal(mission, state.Mission);
        Assert.Equal(missionId, state.MissionId);
        // Offering a definition without a bound run is legal and keeps the definition.
        var offered = new BarPatronState(id, "station", "Contact", "Description", "seed", mission);
        Assert.Equal(mission, offered.Mission);
        Assert.Null(offered.MissionId);
        // A bound run still needs the definition it runs, a nonempty identity, and its own owner.
        Assert.Throws<ArgumentException>(() => new BarPatronState(id, "station", "Contact", "Description", "seed", mission, Guid.Empty));
        Assert.Throws<ArgumentException>(() => new BarPatronState(id, "station", "Contact", "Description", "seed", null, missionId));
        Assert.Throws<ArgumentException>(() => new BarPatronState(id, "station", "Contact", "Description", "seed", new StoryMissionDefinitionId("jobs", "mission-x"), missionId));
    }

    [Fact]
    public void TextBoundsUseStrictUtf8AndDoNotRewriteLocalizedNames()
    {
        var id = new BarPatronId("campaign", "contact");
        Assert.Equal("Élodie", new BarPatronState(id, "station", "Élodie", "Description", "seed").Name);
        Assert.Throws<ArgumentException>(() => new BarPatronState(id, "station", new string('é', 65), "Description", "seed"));
        Assert.ThrowsAny<ArgumentException>(() => new BarPatronState(id, "station", "\ud800", "Description", "seed"));
        Assert.Throws<ArgumentException>(() => new BarPatronState(id, "station", "A\nB", "Description", "seed"));
    }
}
