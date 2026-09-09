using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>Restricted native state inspection for the empty-Combat qualification profile; not runtime qualification.</summary>
internal sealed class WorldEmptyCombatProfile
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private readonly Type _combat;
    private readonly List<FieldInfo> _empty = new(), _null = new(), _zero = new();
    private readonly FieldInfo _asteroids, _initialized;
    internal WorldEmptyCombatProfile(Assembly assembly) : this(assembly.GetType("Source.Galaxy.POI.Combat", true)!, assembly.GetType("Source.Galaxy.MapPointOfInterest", true)!) { }
    internal WorldEmptyCombatProfile(Type combat, Type poi)
    {
        _combat = combat;
        if (combat.BaseType != poi) throw new InvalidDataException("Unexpected Combat inheritance.");
        FieldInfo Field(string name) => poi.GetField(name, Fields) ?? throw new MissingFieldException(poi.FullName, name);
        foreach (string name in new[] { "persistables", "units", "payloads", "guardDescriptors", "deadUnitIdentities", "unitOverlay", "cargoDescriptors", "deadPersistableIdentities", "persistableOverlay", "salvageDescriptors", "deadSalvageIdentities", "salvageOverlay", "_pendingStationBuildings" })
        {
            var field = Field(name); var type = field.FieldType;
            if (!type.IsGenericType || (type.GetGenericTypeDefinition() != typeof(List<>) && type.GetGenericTypeDefinition() != typeof(Dictionary<,>) && type.GetGenericTypeDefinition() != typeof(HashSet<>)))
                throw new InvalidDataException("Unexpected empty-world collection shape.");
            _empty.Add(field);
        }
        foreach (string name in new[] { "hazardFieldData", "oreOwnershipOverride", "oreOwnershipOverrideItem", "storyteller", "linkedJumpgatePassGuid", "<customFieldData>k__BackingField" }) _null.Add(Field(name));
        foreach (string name in new[] { "nextPayloadSequenceId", "nextCargoSlotId" })
        {
            var field = Field(name); if (field.FieldType != typeof(int)) throw new InvalidDataException("Unexpected empty-world counter shape.");
            _zero.Add(field);
        }
        _asteroids = Field("<hasAsteroids>k__BackingField");
        _initialized = Field("asteroidsInitialized");
        if (_asteroids.FieldType != typeof(bool) || _initialized.FieldType != typeof(bool)) throw new InvalidDataException("Unexpected asteroid flag shape.");
    }
    internal void Require(object value)
    {
        if (value.GetType() != _combat) throw new InvalidDataException("Empty-world profile requires exact native Combat.");
        foreach (var field in _empty)
        {
            var collection = field.GetValue(value);
            if (collection == null && field.Name == "_pendingStationBuildings") continue;
            if (collection == null || collection.GetType() != field.FieldType || (int)field.FieldType.GetProperty("Count")!.GetValue(collection)! != 0)
                throw new InvalidDataException("Executable world content is outside the empty qualification profile.");
        }
        foreach (var field in _null) if (field.GetValue(value) != null) throw new InvalidDataException("World attachment is outside the empty qualification profile.");
        foreach (var field in _zero) if ((int)field.GetValue(value)! != 0) throw new InvalidDataException("World generation state is outside the empty qualification profile.");
        if ((bool)_asteroids.GetValue(value)! || (bool)_initialized.GetValue(value)!) throw new InvalidDataException("Asteroids are outside the empty qualification profile.");
    }
}
