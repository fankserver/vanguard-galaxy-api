using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace VGModAPI.Core.Integration;

internal sealed class WorldParsedNode
{
    internal object Json { get; }
    internal string NativeId { get; }
    internal string SystemId { get; }
    internal string Digest { get; }
    internal WorldParsedNode(object json, string nativeId, string systemId, string digest)
    { Json = json; NativeId = nativeId; SystemId = systemId; Digest = digest; }
}

/// <summary>Inspects the serialized galaxy before native constructors. Does not invoke a vanilla type factory.</summary>
internal sealed class WorldJsonInspection
{
    private readonly PropertyInfo _item, _isObject, _object, _isArray, _array, _isString, _string;
    private readonly Type _objectType;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaxVisited = 100000;
    internal WorldJsonInspection(Assembly assembly)
    {
        _objectType = assembly.GetType("LightJson.JsonObject", true)!;
        var value = assembly.GetType("LightJson.JsonValue", true)!;
        _item = _objectType.GetProperty("Item", new[] { typeof(string) }) ?? throw new MissingMemberException("JsonObject.Item");
        _isObject = Property(value, "IsJsonObject"); _object = Property(value, "AsJsonObject");
        _isArray = Property(value, "IsJsonArray"); _array = Property(value, "AsJsonArray");
        _isString = Property(value, "IsString"); _string = Property(value, "AsString");
    }
    private static PropertyInfo Property(Type type, string name) => type.GetProperty(name) ?? throw new MissingMemberException(type.FullName, name);
    private object Field(object json, string key) => _item.GetValue(json, new object[] { key })!;
    private object Object(object value) => (bool)_isObject.GetValue(value)! ? _object.GetValue(value)! : throw new InvalidDataException("Expected world JSON object.");
    private IEnumerable Array(object value) => (bool)_isArray.GetValue(value)! ? (IEnumerable)_array.GetValue(value)! : throw new InvalidDataException("Expected world JSON array.");
    private string Text(object json, string key)
    {
        var value = Field(json, key);
        if (!(bool)_isString.GetValue(value)!) throw new InvalidDataException("Expected world identity string.");
        var text = (string)_string.GetValue(value)!;
        if (string.IsNullOrEmpty(text) || Utf8.GetByteCount(text) > 128) throw new InvalidDataException("Invalid world identity length.");
        return text;
    }

    internal WorldParsedNode[] Read(object root)
    {
        if (!_objectType.IsInstanceOfType(root)) throw new InvalidDataException("Expected native save JSON root.");
        var map = Object(Field(Object(Field(root, "Player")), "map"));
        var result = new List<WorldParsedNode>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var systemIds = new HashSet<string>(StringComparer.Ordinal);
        int visited = 0;
        // The native loader also supports a single-sector map represented directly by its systems.
        if ((bool)_isArray.GetValue(Field(map, "systems"))!) ReadSystems(map);
        else foreach (var sector in Array(Field(map, "sectors"))) { Visit(); ReadSystems(Object(sector)); }
        return result.ToArray();

        void Visit() { if (++visited > MaxVisited) throw new InvalidDataException("World inspection exceeds its node budget."); }
        void ReadSystems(object sector)
        {
            foreach (var value in Array(Field(sector, "systems")))
            {
                Visit(); var system = Object(value); string systemId = Text(system, "guid");
                if (!systemIds.Add(systemId)) throw new InvalidDataException("Ambiguous parent system identity.");
                foreach (var entry in Array(Field(system, "pointsOfInterest")))
                {
                    Visit(); var poi = Object(entry); string id = Text(poi, "guid");
                    if (!WorldObjectIdentity.IsReserved(id)) continue;
                    if (result.Count >= WorldSerializationAssociation.MaxObjects || !ids.Add(id)) throw new InvalidDataException("Duplicate or excessive owned POIs.");
                    if (Text(poi, "type") != "Combat" || Text(poi, "systemName") != systemId)
                        throw new InvalidDataException("Owned POI type or parent link is not supported.");
                    result.Add(new WorldParsedNode(poi, id, systemId, Digest(poi)));
                }
            }
        }
    }

    internal static string Digest(object json)
    {
        var text = json.ToString() ?? throw new InvalidDataException("Missing native JSON text.");
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("Owned POI serialization exceeds its inspection limit.");
        return GenerationStore.Hash(Utf8.GetBytes(text));
    }

    internal static WorldConstructionNode[] Bind(WorldSavedObject[] rows, WorldParsedNode[] nodes)
    {
        if (rows == null || nodes == null || rows.Length != nodes.Length || rows.Length > WorldSerializationAssociation.MaxObjects)
            throw new InvalidDataException("World metadata/native inventory mismatch.");
        var inventory = new Dictionary<string, WorldSavedObject>(StringComparer.Ordinal);
        foreach (var row in rows)
            if (row == null || !inventory.TryAdd(row.Identity.NativeId, row)) throw new InvalidDataException("Duplicate world metadata identity.");
        var result = new WorldConstructionNode[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            if (node == null || !inventory.TryGetValue(node.NativeId, out var row) || node.SystemId != row.SystemId || node.Digest != row.NativeDigest)
                throw new InvalidDataException("World node does not match its committed metadata.");
            inventory.Remove(node.NativeId);
            result[i] = new WorldConstructionNode(node.Json, row.Identity, node.Digest);
        }
        return result;
    }
}
