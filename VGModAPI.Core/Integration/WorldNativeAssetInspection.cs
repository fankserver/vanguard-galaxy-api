using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>Checks current native registry membership without calling builders or generating equipment.</summary>
internal sealed class WorldNativeAssetInspection
{
    private readonly Assembly _assembly;
    private readonly Dictionary<(FieldInfo Field, string Id), (IDictionary Registry, object Value)> _references = new();
    internal void Validate()
    {
        foreach (var entry in _references)
        {
            RequireAlive(entry.Value.Value);
            if (!ReferenceEquals(entry.Key.Field.GetValue(null), entry.Value.Registry) || entry.Value.Registry.Count > 10000 ||
                !entry.Value.Registry.Contains(entry.Key.Id) || !ReferenceEquals(entry.Value.Registry[entry.Key.Id], entry.Value.Value))
                throw new InvalidDataException("Native asset registry changed after inspection.");
        }
    }
    private static void RequireAlive(object value)
    {
        var type = value.GetType();
        while (type != null && type.FullName != "UnityEngine.Object") type = type.BaseType;
        var pointer = type?.GetField("m_CachedPtr", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (pointer?.FieldType != typeof(IntPtr) || (IntPtr)pointer.GetValue(value)! == IntPtr.Zero)
            throw new InvalidDataException("Native asset is destroyed or its Unity lifetime cannot be inspected.");
    }
    internal WorldNativeAssetInspection(Assembly assembly) => _assembly = assembly;
    internal void Ship(string id) => Require("Behaviour.Unit.SpaceShip", "allShips", id);
    internal void Equipment(string id) => Require("Behaviour.Equipment.Builder.EquipmentBuilder", "allBuilders", id);
    private void Require(string typeName, string fieldName, string id)
    {
        var type = _assembly.GetType(typeName, false);
        var field = type?.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        var expected = type == null ? null : typeof(Dictionary<,>).MakeGenericType(typeof(string), type);
        if (field == null || field.FieldType != expected || field.GetValue(null) is not IDictionary registry || registry.Count > 10000 || !registry.Contains(id))
            throw new InvalidDataException("Required native asset registry entry is unavailable.");
        var value = registry[id];
        if (value == null || !type!.IsInstanceOfType(value) || value.GetType().Assembly != _assembly)
            throw new InvalidDataException("Provider-defined asset types are not admitted as native content.");
        RequireAlive(value);
        var key = (field, id);
        if (_references.TryGetValue(key, out var prior))
        {
            if (!ReferenceEquals(prior.Registry, registry) || !ReferenceEquals(prior.Value, value))
                throw new InvalidDataException("Native asset changed during inspection.");
        }
        else
        {
            if (_references.Count >= 10000) throw new InvalidDataException("Excessive native asset references.");
            _references.Add(key, (registry, value));
        }
    }
}
