using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI;

/// <summary>Names match the inspected game's compartment catalog; values are not native enum casts.</summary>
public enum CompartmentType
{
    Airlock, Corridor, Armory, CargoHold, Bridge, LivingQuarters, ReactorRoom, MessHall, Brig,
    OfficersLounge, ControlRoom, StorageBay, Workshop, Laboratory, OreProcessing, Excavation,
    DebrisField, GeneratorRoom, ServerRoom, SecurityOffice, MedicalBay
}

/// <summary>One immutable owned compartment. Crew identifiers are checked against the native catalog on registration.</summary>
public sealed class DungeonCompartmentDefinition
{
    public string Id { get; }
    public CompartmentType Type { get; }
    public bool Locked { get; }
    public IReadOnlyList<string> Adjacent { get; }
    public IReadOnlyDictionary<string, int> Defenders { get; }
    public DungeonCompartmentDefinition(string id, CompartmentType type, IEnumerable<string> adjacent,
        bool locked = false, IEnumerable<KeyValuePair<string, int>>? defenders = null)
    {
        DungeonLayoutIds.Check(id);
        if (!Enum.IsDefined(typeof(CompartmentType), type)) throw new ArgumentOutOfRangeException(nameof(type));
        var neighbors = (adjacent ?? throw new ArgumentNullException(nameof(adjacent))).ToArray();
        if (neighbors.Length > 8 || neighbors.Distinct(StringComparer.Ordinal).Count() != neighbors.Length) throw new ArgumentException("At most eight distinct adjacent compartments supported.");
        foreach (var neighbor in neighbors) { DungeonLayoutIds.Check(neighbor); if (neighbor == id) throw new ArgumentException("Self edges are not supported."); }
        var crew = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in defenders ?? Array.Empty<KeyValuePair<string, int>>())
        {
            DungeonNativeIds.Check(pair.Key);
            if (pair.Value < 1 || pair.Value > 100) throw new ArgumentOutOfRangeException(nameof(defenders));
            crew.Add(pair.Key, pair.Value);
        }
        if (crew.Count > 32 || crew.Values.Sum() > 100) throw new ArgumentException("At most 100 defenders per compartment supported.");
        Id = id; Type = type; Locked = locked;
        Adjacent = Array.AsReadOnly(neighbors); Defenders = new ReadOnlyDictionary<string, int>(crew);
    }
}

/// <summary>A connected, symmetric compartment graph with one unlocked airlock. Ordering defines native compartment indices.</summary>
public sealed class DungeonLayout
{
    public IReadOnlyList<DungeonCompartmentDefinition> Compartments { get; }
    public DungeonLayout(IEnumerable<DungeonCompartmentDefinition> compartments)
    {
        var rooms = (compartments ?? throw new ArgumentNullException(nameof(compartments))).ToArray();
        if (rooms.Length < 2 || rooms.Length > 64 || rooms.Any(r => r == null)) throw new ArgumentException("A layout requires 2–64 compartments.");
        var byId = new Dictionary<string, DungeonCompartmentDefinition>(StringComparer.Ordinal);
        foreach (var room in rooms) if (!byId.TryAdd(room.Id, room)) throw new ArgumentException("Duplicate compartment ID.");
        var airlocks = rooms.Where(r => r.Type == CompartmentType.Airlock).ToArray();
        if (airlocks.Length != 1 || airlocks[0].Locked) throw new ArgumentException("Exactly one unlocked airlock required.");
        if (rooms.Sum(r => r.Defenders.Values.Sum()) > 512) throw new ArgumentException("At most 512 defenders per layout supported.");
        foreach (var room in rooms)
            foreach (var neighbor in room.Adjacent)
                if (!byId.TryGetValue(neighbor, out var adjacent) || !adjacent.Adjacent.Contains(room.Id, StringComparer.Ordinal)) throw new ArgumentException("Edges must reference existing compartments and be symmetric.");
        var seen = new HashSet<string>(StringComparer.Ordinal); var pending = new Queue<string>(); pending.Enqueue(airlocks[0].Id);
        while (pending.Count > 0)
        {
            var id = pending.Dequeue(); if (!seen.Add(id)) continue;
            foreach (var neighbor in byId[id].Adjacent) pending.Enqueue(neighbor);
        }
        if (seen.Count != rooms.Length) throw new ArgumentException("Every compartment must connect to the airlock.");
        Compartments = Array.AsReadOnly(rooms);
    }
}

/// <summary>Native catalog keys are not owned identifiers; preserve spaces and punctuation verbatim.</summary>
internal static class DungeonNativeIds
{
    internal static void Check(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl))
            throw new ArgumentException("Native identifiers require 1–128 non-control characters.", nameof(id));
    }
}

internal static class DungeonLayoutIds
{
    internal static void Check(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')))
            throw new ArgumentException("Identifiers require 1–128 letters, digits, underscores, hyphens or dots.", nameof(id));
    }
}
