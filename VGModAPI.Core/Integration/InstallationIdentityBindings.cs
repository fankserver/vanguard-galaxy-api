using System;
using System.Collections;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class InstallationIdentityBindings
{
    private readonly PropertyInfo _current;
    private readonly FieldInfo _persistables;
    private readonly MethodInfo _find;
    private readonly Type _location;
    internal InstallationIdentityBindings(Assembly assembly)
    {
        var map = assembly.GetType("Source.Galaxy.GalaxyMapData", true)!;
        var poi = assembly.GetType("Source.Galaxy.MapPointOfInterest", true)!;
        _location = assembly.GetType(BindingCatalog.BoardingLocation, true)!;
        _current = map.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMemberException(map.FullName, "current");
        _find = map.GetMethod("GetPointOfInterest", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            null, new[] { typeof(string) }, null) ?? throw new MissingMethodException(map.FullName, "GetPointOfInterest");
        _persistables = poi.GetField("persistables", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            ?? throw new MissingFieldException(poi.FullName, "persistables");
        if (_current.PropertyType != map || _current.GetMethod == null || !_current.GetMethod.IsStatic ||
            _current.GetIndexParameters().Length != 0 || _find.ReturnType != poi ||
            !NativeTypeName.Matches(_persistables.FieldType, "System.Collections.Generic.List`1<Source.Data.Persistable.PersistableData>"))
            throw new MissingMemberException("Installation identity bindings do not match the supported game.");
    }
    internal bool Contains(string poiId, object location)
    {
        if (!_location.IsInstanceOfType(location)) return false;
        var map = _current.GetValue(null);
        var poi = map == null ? null : _find.Invoke(map, new object[] { poiId });
        // GetPersistables generates content. Identity lookup must only inspect already-existing membership.
        return poi != null && _persistables.GetValue(poi) is IList entries && entries.Contains(location);
    }
}
