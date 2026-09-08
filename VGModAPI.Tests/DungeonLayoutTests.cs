using System;
using VGModAPI;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonLayoutTests
{
    [Fact]
    public void ConnectedLayoutCopiesInputAndPreservesNativeCompartmentNames()
    {
        var adjacent = new[] { "bridge" };
        var entry = new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, adjacent);
        adjacent[0] = "changed";
        var layout = new DungeonLayout(new[] { entry, new DungeonCompartmentDefinition("bridge", CompartmentType.Bridge, new[] { "entry" }) });
        Assert.Equal("bridge", layout.Compartments[0].Adjacent[0]); Assert.Equal("Bridge", layout.Compartments[1].Type.ToString());
    }
    [Fact]
    public void MissingAsymmetricAndDisconnectedEdgesAreRejected()
    {
        var entry = new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, new[] { "room" });
        Assert.Throws<ArgumentException>(() => new DungeonLayout(new[] { entry, new DungeonCompartmentDefinition("room", CompartmentType.Corridor, Array.Empty<string>()) }));
        Assert.Throws<ArgumentException>(() => new DungeonLayout(new[] { entry, new DungeonCompartmentDefinition("different", CompartmentType.Corridor, Array.Empty<string>()) }));
        Assert.Throws<ArgumentException>(() => new DungeonLayout(new[] { new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, Array.Empty<string>()), new DungeonCompartmentDefinition("room", CompartmentType.Corridor, Array.Empty<string>()) }));
    }
    [Fact]
    public void LockedEntryAndDuplicateRoomIdsAreRejected()
    {
        var room = new DungeonCompartmentDefinition("room", CompartmentType.Corridor, new[] { "entry" });
        Assert.Throws<ArgumentException>(() => new DungeonLayout(new[] { new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, new[] { "room" }, true), room }));
        Assert.Throws<ArgumentException>(() => new DungeonLayout(new[] { room, room }));
    }
}
