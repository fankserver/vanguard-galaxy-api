using System;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>Metadata-only validation of inspected nested factory selectors; never invokes their constructors.</summary>
internal sealed class WorldNestedTypeCatalog
{
    private readonly Assembly _assembly;
    internal WorldNestedTypeCatalog(Assembly assembly) => _assembly = assembly;
    internal void Persistable(string name) => Require(name, "Source.Data.Persistable.", "Source.Data.Persistable.PersistableData", Type.EmptyTypes);
    internal void Descriptor(string name) => Require(name, "Source.Galaxy.", "Source.Galaxy.UnitGenerationDescriptor", Type.EmptyTypes);
    internal void Storyteller(string name) => Require(name, "Source.Simulation.World.POI.", "Source.Simulation.World.PoiStoryteller",
        new[] { _assembly.GetType("Source.Galaxy.MapPointOfInterest", true)! });
    internal static void Unit(string name)
    {
        if (name != "SpaceShip" && name != "Turret" && name != "CombatStationPart") throw new InvalidDataException("Unsupported native unit selector.");
    }
    private void Require(string name, string prefix, string baseName, Type[] arguments)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128) throw new InvalidDataException("Invalid nested native selector.");
        foreach (char c in name)
            if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_')
                throw new InvalidDataException("Qualified or executable-provider type selectors are not supported.");
        var type = _assembly.GetType(prefix + name, false);
        var parent = _assembly.GetType(baseName, false);
        if (type == null || parent == null || type.Assembly != _assembly || type.IsAbstract || !type.IsSubclassOf(parent) || type.GetConstructor(arguments) == null)
            throw new InvalidDataException("Nested selector is not an inspected native factory type.");
    }
}
