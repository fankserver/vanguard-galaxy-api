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
        var occurrence = Guid.NewGuid();
        var definition = new BarPatronDefinition("contact", "CustomAct3RickoStation", "Élodie", "Contact description", "stable-seed",
            mission: mission, occurrence: occurrence);
        Assert.Equal("Élodie", definition.Name);
        Assert.Equal("CustomAct3RickoStation", definition.StationId);
        Assert.Equal(BarPatronRetention.Persistent, definition.Retention);
        Assert.Equal(mission, definition.Mission);
        Assert.Equal(occurrence, definition.Occurrence);
    }

    [Fact]
    public void InvalidReferencesRetentionAndTextAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new BarPatronDefinition("contact", "station", "Name", "Description", "seed", mission: new StoryContentId("author", "job")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BarPatronDefinition("contact", "station", "Name", "Description", "seed", (BarPatronRetention)42));
        Assert.Throws<ArgumentException>(() => new BarPatronDefinition("contact", "station", new string('é', 65), "Description", "seed"));
        Assert.ThrowsAny<ArgumentException>(() => new BarPatronDefinition("contact", "station", "\ud800", "Description", "seed"));
    }
}
