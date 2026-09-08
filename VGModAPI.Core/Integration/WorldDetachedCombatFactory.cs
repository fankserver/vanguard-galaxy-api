using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>Builds a detached native Combat without SetupPOI, faction creation, naming getters or global seeded randomness.</summary>
internal sealed class WorldDetachedCombatFactory
{
    private readonly Type _systemType;
    private readonly ConstructorInfo _combat;
    private readonly FieldInfo _guid, _name, _system, _position, _level, _faction, _factions, _points, _x, _y, _background, _content;
    internal WorldDetachedCombatFactory(Assembly assembly)
    {
        var element = assembly.GetType("Source.Galaxy.MapElement", true)!;
        var poi = assembly.GetType("Source.Galaxy.MapPointOfInterest", true)!;
        _systemType = assembly.GetType("Source.Galaxy.SystemMapData", true)!;
        var combat = assembly.GetType("Source.Galaxy.POI.Combat", true)!;
        _combat = combat.GetConstructor(Type.EmptyTypes) ?? throw new MissingMethodException("Combat constructor");
        _guid = Field(element, "<guid>k__BackingField"); _name = Field(element, "_name");
        _system = Field(element, "system"); _position = Field(element, "position"); _level = Field(element, "level");
        _faction = Field(element, "<faction>k__BackingField");
        _factions = Field(assembly.GetType("Source.Galaxy.Faction", true)!, "allFactions");
        _points = Field(_systemType, "pointsOfInterest");
        _x = Field(_position.FieldType, "x"); _y = Field(_position.FieldType, "y");
        _background = Field(poi, "backgroundSeed"); _content = Field(poi, "contentSeed");
        foreach (var field in new[] { _guid, _name, _system, _position, _level, _faction, _points, _x, _y, _background, _content })
            if (field.IsStatic) throw new MissingFieldException("World instance field became static: " + field.Name);
        if (!_factions.IsStatic || combat.BaseType != poi || _guid.FieldType != typeof(string) || _name.FieldType != typeof(string) ||
            _level.FieldType != typeof(int) || _x.FieldType != typeof(float) || _y.FieldType != typeof(float) ||
            _background.FieldType != typeof(ulong) || _content.FieldType != typeof(ulong))
            throw new MissingFieldException("World construction field shape changed.");
    }
    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        ?? throw new MissingFieldException(type.FullName, name);

    internal object Create(WorldSavedDefinition saved, WorldObjectIdentity identity, object system, float x, float y)
    {
        if (saved == null || identity == null || saved.Owner != identity.Owner || saved.Definition.LocalId != identity.LocalId)
            throw new InvalidDataException("World declaration/instance ownership mismatch.");
        if (!_systemType.IsInstanceOfType(system) || !Finite(x) || !Finite(y) || Math.Abs(x) > 1000000 || Math.Abs(y) > 1000000)
            throw new InvalidDataException("Invalid world placement.");
        var factions = _factions.GetValue(null) as IDictionary ?? throw new InvalidDataException("Native faction catalog unavailable.");
        var faction = factions[saved.Definition.FactionId] ?? throw new InvalidDataException("World creation cannot initialize an absent faction.");
        var points = _points.GetValue(system) as IList ?? throw new InvalidDataException("Native system membership unavailable.");
        if (points.Count > 10000) throw new InvalidDataException("System membership exceeds inspection bound.");
        for (int i = 0; i < points.Count; i++)
        {
            var existing = points[i] ?? throw new InvalidDataException("Null native POI.");
            if ((string?)_guid.GetValue(existing) == identity.NativeId) throw new InvalidDataException("World identity already exists in the system.");
            var position = _position.GetValue(existing)!;
            float px = (float)_x.GetValue(position)!, py = (float)_y.GetValue(position)!;
            if (!Finite(px) || !Finite(py)) throw new InvalidDataException("Invalid neighbouring POI position.");
            double dx = (double)px - x, dy = (double)py - y;
            if (dx * dx + dy * dy < 4) throw new InvalidDataException("Placement would displace a neighbouring POI.");
        }
        var result = _combat.Invoke(null);
        var vector = Activator.CreateInstance(_position.FieldType)!; _x.SetValue(vector, x); _y.SetValue(vector, y);
        _guid.SetValue(result, identity.NativeId); _name.SetValue(result, saved.Definition.Name);
        _system.SetValue(result, system); _position.SetValue(result, vector); _level.SetValue(result, saved.Definition.Level); _faction.SetValue(result, faction);
        // Native loading truncates these ulong fields to uint; use the representable range deliberately.
        string hash = identity.NativeId.Substring(WorldObjectIdentity.ReservedPrefix.Length + 3);
        _background.SetValue(result, (ulong)uint.Parse(hash.Substring(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        _content.SetValue(result, (ulong)uint.Parse(hash.Substring(8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return result;
    }
    internal bool MatchesCreated(object poi, WorldSavedDefinition saved, WorldObjectIdentity identity, object system, float x, float y)
    {
        var position = _position.GetValue(poi)!;
        var factions = _factions.GetValue(null) as IDictionary;
        var faction = factions?[saved.Definition.FactionId];
        return faction != null && ReferenceEquals(_faction.GetValue(poi), faction) &&
            (string?)_guid.GetValue(poi) == identity.NativeId && (string?)_name.GetValue(poi) == saved.Definition.Name &&
            (int)_level.GetValue(poi)! == saved.Definition.Level && ReferenceEquals(_system.GetValue(poi), system) &&
            (float)_x.GetValue(position)! == x && (float)_y.GetValue(position)! == y;
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
