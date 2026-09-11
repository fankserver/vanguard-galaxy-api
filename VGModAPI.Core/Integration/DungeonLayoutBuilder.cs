using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Builds detached native layout objects; publication is left to the inspected creation boundary.</summary>
internal sealed class DungeonLayoutBuilder
{
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly Type _room, _crew;
    internal DungeonLayoutBuilder(GameBindings game, IBoardingTacticalNativeBindings native)
        : this(native, game.Assembly.GetType(DungeonNativeSchema.Room, true)!, game.Assembly.GetType(DungeonNativeSchema.Crew, true)!) { }
    internal DungeonLayoutBuilder(IBoardingTacticalNativeBindings native, Type room, Type crew)
    {
        _native = native; _room = room; _crew = crew;
        foreach (var type in new[] { _room, _crew })
            if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null) throw new MissingMethodException(type.FullName, ".ctor");
    }
    internal IList Rooms(DungeonLayout layout, int sizeTier)
    {
        // Vanilla placement requires the airlock at index zero, regardless of author input order.
        var ordered = layout.Compartments.OrderBy(c => c.Type == CompartmentType.Airlock ? 0 : 1).ToArray();
        var indices = ordered.Select((room, index) => (room.Id, index)).ToDictionary(p => p.Id, p => p.index, StringComparer.Ordinal);
        var rooms = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_room))!;
        for (var index = 0; index < ordered.Length; index++)
        {
            var definition = ordered[index]; var room = Activator.CreateInstance(_room)!;
            _native.Set(room, "authoredRoomType", definition.Type.ToString()); _native.Set(room, "authoredRoomIndex", index);
            _native.Set(room, "authoredRoomState", "Unknown"); _native.Set(room, "authoredRoomLocked", definition.Locked); _native.Set(room, "authoredRoomWasLocked", definition.Locked);
            _native.Set(room, "authoredRoomNeighbors", definition.Adjacent.Select(id => indices[id]).ToList());
            var capacity = (int)_native.Call("dungeonRoomCapacity", room, sizeTier)!;
            if (definition.Defenders.Values.Sum() > capacity) throw new ArgumentException("Resource defenders exceed this target's native compartment capacity.");
            rooms.Add(room);
        }
        return rooms;
    }
    internal IList Defenders(DungeonLayout layout, float healthMultiplier)
    {
        var units = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_crew))!;
        var ordered = layout.Compartments.OrderBy(c => c.Type == CompartmentType.Airlock ? 0 : 1).ToArray();
        for (var index = 0; index < ordered.Length; index++)
            foreach (var pair in ordered[index].Defenders)
                for (var count = 0; count < pair.Value; count++)
                {
                    var crew = Activator.CreateInstance(_crew)!;
                    _native.Set(crew, "authoredCrewType", pair.Key); _native.Set(crew, "authoredCrewFriendly", false); _native.Set(crew, "authoredCrewRoom", index);
                    _native.Call("dungeonCrewHealth", crew, healthMultiplier); units.Add(crew);
                }
        return units;
    }
}
