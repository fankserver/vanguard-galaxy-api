using System;
using System.Collections.Generic;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonLayoutBuilderTests
{
    public sealed class NativeObject
    {
        public Dictionary<string, object?> Fields { get; } = new();
    }
    internal sealed class Native : IBoardingTacticalNativeBindings
    {
        public object? Player => null;
        public object? Manager => null;
        public object? Get(object? obj, string key) => obj is NativeObject value ? value.Fields[key] : null;
        public void Set(object obj, string key, object? value) => ((NativeObject)obj).Fields[key] = value;
        public object? Call(string key, object? target, params object[] arguments)
        {
            if (key == "resumeDocking") { ((Action)Get(target, "restoreDocking")!)(); return null; }
            if (key == "walkManifest") return Get(target, "walkManifest");
            if (key == "dungeonProfile") return arguments[0];
            if (key == "dungeonNoScuttleProfile") return "protected:" + arguments[0];
            if (key == "dungeonRoomCapacity") return 5;
            if (key == "dungeonCrewHealth") { Set(target!, "health", arguments[0]); return null; }
            throw new InvalidOperationException(key);
        }
        public object EnumArgument(string key, int index, string name) => throw new NotSupportedException();
        public object OutcomeReason(string name) => throw new NotSupportedException();
        public bool ValidCrew(string id) => true;
        public object CreateOptions(BoardingCrewManifest crew, BoardingCommandOptions options) => throw new NotSupportedException();
        public void ApplyOptions(object native, BoardingCommandOptions options) => throw new NotSupportedException();
    }
    private static DungeonLayout Layout(int defenders) => new(new[]
    {
        new DungeonCompartmentDefinition("hold", CompartmentType.CargoHold, new[] { "entry" }, true, new Dictionary<string, int> { ["Marine"] = defenders }),
        new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, new[] { "hold" })
    });
    [Fact]
    public void DetachedNativeRoomsAndDefendersUseSameAirlockFirstIndexMapping()
    {
        var native = new Native(); var builder = new DungeonLayoutBuilder(native, typeof(NativeObject), typeof(NativeObject));
        var layout = Layout(2); var rooms = builder.Rooms(layout, 2); var units = builder.Defenders(layout, 1.5f);
        Assert.Equal("Airlock", native.Get(rooms[0], "authoredRoomType"));
        Assert.Equal(new[] { 1 }, Assert.IsType<List<int>>(native.Get(rooms[0], "authoredRoomNeighbors")));
        Assert.Equal(new[] { 0 }, Assert.IsType<List<int>>(native.Get(rooms[1], "authoredRoomNeighbors")));
        Assert.Equal(true, native.Get(rooms[1], "authoredRoomLocked")); Assert.Equal("Unknown", native.Get(rooms[1], "authoredRoomState"));
        Assert.Equal(2, units.Count);
        foreach (var unit in units)
        {
            Assert.Equal(1, native.Get(unit, "authoredCrewRoom")); Assert.Equal("Marine", native.Get(unit, "authoredCrewType"));
            Assert.Equal(false, native.Get(unit, "authoredCrewFriendly")); Assert.Equal(1.5f, native.Get(unit, "health"));
        }
    }
    [Fact]
    public void NativeCapacityRefusesOtherwiseStructurallyValidLayout()
    {
        var builder = new DungeonLayoutBuilder(new Native(), typeof(NativeObject), typeof(NativeObject));
        Assert.Throws<ArgumentException>(() => builder.Rooms(Layout(6), 1));
    }
}
