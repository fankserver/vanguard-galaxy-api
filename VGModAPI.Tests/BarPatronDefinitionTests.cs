using System;
using VGModAPI;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarPatronDefinitionTests
{
    [Fact]
    public void ContactRetainsLocalizedDataWithoutNativeObjects()
    {
        var mission = new StoryContentId("author", "job");
        var definition = new BarPatronDefinition("contact", "CustomAct3RickoStation", "Élodie", "Contact description", new BarPatronPresentation("stable-seed"),
            mission: mission);
        Assert.Equal("Élodie", definition.Name);
        Assert.Equal("CustomAct3RickoStation", definition.StationId);
        Assert.Equal(BarPatronRetention.Persistent, definition.Retention);
        Assert.Equal(mission, definition.Mission);
    }

    [Fact]
    public void InvalidReferencesRetentionAndTextAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarPatronDefinition("contact", "station", "Name", "Description", new BarPatronPresentation("seed"), (BarPatronRetention)42));
        Assert.Throws<ArgumentException>(() => new BarPatronDefinition("contact", "station", new string('é', 65), "Description", new BarPatronPresentation("seed")));
        Assert.ThrowsAny<ArgumentException>(() => new BarPatronDefinition("contact", "station", "\ud800", "Description", new BarPatronPresentation("seed")));
    }
}
