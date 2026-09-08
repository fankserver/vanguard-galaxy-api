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
    }
}
