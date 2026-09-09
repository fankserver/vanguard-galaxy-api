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
    private readonly Action? _validateAssets;
    internal void ValidateAssets() => _validateAssets?.Invoke();
    internal WorldParsedNode(object json, string nativeId, string systemId, string digest, Action? validateAssets = null)
    { Json = json; NativeId = nativeId; SystemId = systemId; Digest = digest; _validateAssets = validateAssets; }
}

/// <summary>Inspects the serialized galaxy before native constructors. Does not invoke a vanilla type factory.</summary>
internal sealed partial class WorldJsonInspection
{
    private readonly PropertyInfo _item, _isObject, _object, _isArray, _array, _isString, _string;
    private readonly Type _objectType;
    private readonly MethodInfo _parse;
    private readonly WorldSaveFormat _format;
    private readonly WorldNestedTypeCatalog _nested;
    private readonly PropertyInfo _isNull;
    internal void StampOwnedPoi(object node, string identity) => _format.StampOwnedPoi(node, identity);
    internal void SealSnapshot(object root, bool owned) => _format.Seal(root, owned);
    internal void UnsealVerified(object root, bool owned) => _format.UnsealVerified(root, owned);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaxVisited = 100000;
    internal WorldJsonInspection(Assembly assembly)
    {
        _format = new WorldSaveFormat(assembly);
        _nested = new WorldNestedTypeCatalog(assembly);
        _objectType = assembly.GetType("LightJson.JsonObject", true)!;
        var value = assembly.GetType("LightJson.JsonValue", true)!;
        _parse = value.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException("JsonValue.Parse");
        _item = _objectType.GetProperty("Item", new[] { typeof(string) }) ?? throw new MissingMemberException("JsonObject.Item");
        _isObject = Property(value, "IsJsonObject"); _object = Property(value, "AsJsonObject");
        _isArray = Property(value, "IsJsonArray"); _array = Property(value, "AsJsonArray");
        _isString = Property(value, "IsString"); _string = Property(value, "AsString");
        _isNull = Property(value, "IsNull");
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

    internal WorldConstructionNode? RequireFactory(WorldConstructionGate gate, Guid session, object value, long providerRevision)
    {
        var json = Object(value);
        string id = Text(json, "guid");
        return gate.RequireFactory(session, json, id, WorldObjectIdentity.IsReserved(id) ? Digest(json) : "", providerRevision);
    }

    internal object ParseCaptured(byte[] nativeBytes) =>
        Object(_parse.Invoke(null, new object[] { WorldLoadBytes.Decode(nativeBytes) })!);

    internal byte[] VerifyInput(byte[] nativeBytes, object root)
    {
        if (!_objectType.IsInstanceOfType(root)) throw new InvalidDataException("Expected native save JSON root.");
        if (nativeBytes == null || nativeBytes.Length > WorldLoadBytes.MaxNativeBytes) throw new InvalidDataException("Invalid native load bytes.");
        var frozen = (byte[])nativeBytes.Clone();
        var parsed = ParseCaptured(frozen);
        if (!string.Equals(parsed.ToString(), root.ToString(), StringComparison.Ordinal))
            throw new InvalidDataException("Parsed load input does not match the captured native bytes.");
        return frozen;
    }

    internal WorldParsedNode[] Read(object root, bool nativeSnapshot = false)
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
        foreach (var node in result) node.ValidateAssets();
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
                    if (!WorldObjectIdentity.IsReserved(id))
                    {
                        var discriminator = Field(poi, "type");
                        if ((bool)_isString.GetValue(discriminator)! && (string?)_string.GetValue(discriminator) == WorldSaveFormat.OwnedCombatType)
                            throw new InvalidDataException("Owned discriminator lacks owned identity.");
                        continue;
                    }
                    if (result.Count >= WorldSerializationAssociation.MaxObjects || !ids.Add(id)) throw new InvalidDataException("Duplicate or excessive owned POIs.");
                    if (Text(poi, "type") != (nativeSnapshot ? "Combat" : WorldSaveFormat.OwnedCombatType) || Text(poi, "systemName") != systemId)
                        throw new InvalidDataException("Owned POI type or parent link is not supported.");
                    var assets = CheckNestedFactories(poi, Visit);
                    result.Add(new WorldParsedNode(poi, id, systemId, Digest(poi), assets.Validate));
                }
            }
        }
    }

    // These are the directly dispatched nested factories in LoadOptionalPoiData/MapTriggeredPayload.
    // Their subtype data, lazy generation and other nested schemas still require separate validation.
    private WorldNativeAssetInspection CheckNestedFactories(object poi, Action visit)
    {
        var assets = new WorldNativeAssetInspection(_objectType.Assembly);
        void OptionalArray(object parent, string key, Action<object> check)
        {
            var value = Field(parent, key);
            if ((bool)_isNull.GetValue(value)!) return;
            foreach (var entry in Array(value)) { visit(); check(Object(entry)); }
        }
        void Bodies(object parent)
        {
            OptionalArray(parent, "persistables", item =>
            {
                _nested.Persistable(Text(item, "type"));
                var hazard = Field(item, "hazard");
                if (!(bool)_isNull.GetValue(hazard)!) { visit(); CheckHazard(Object(hazard)); }
            });
            OptionalArray(parent, "units", item =>
            {
                var kind = Text(item, "type"); WorldNestedTypeCatalog.Unit(kind);
                if (kind == "SpaceShip") assets.Ship(Text(item, "shipClass"));
                CheckFaction(item, "faction", assets);
                CheckAutoActions(item);
            });
        }
        Bodies(poi);
        CheckFaction(poi, "faction", assets);
        CheckFaction(poi, "oreOwnershipOverride", assets);
        OptionalArray(poi, "guardDescriptors", item => CheckDescriptor(item, assets));
        OptionalArray(poi, "payloads", item =>
        {
            Bodies(item);
            var descriptor = Field(item, "descriptor");
            if (!(bool)_isNull.GetValue(descriptor)!) { visit(); CheckDescriptor(Object(descriptor), assets); }
        });
        var field = Field(poi, "hazardFieldData");
        if (!(bool)_isNull.GetValue(field)!) { visit(); CheckHazardField(Object(field)); }
        var storyteller = Field(poi, "storyteller");
        if (!(bool)_isNull.GetValue(storyteller)!) { visit(); _nested.Storyteller(Text(Object(storyteller), "identifier")); }
        return assets;
    }

    private void CheckFaction(object data, string key, WorldNativeAssetInspection assets)
    {
        if (!(bool)_isNull.GetValue(Field(data, key))!) assets.Faction(Text(data, key));
    }

    private void CheckAutoActions(object data)
    {
        if (!(bool)_isNull.GetValue(Field(data, "autoActions"))!) _nested.AutoActions(Text(data, "autoActions"));
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
            result[i] = new WorldConstructionNode(node.Json, row.Identity, node.Digest, node.ValidateAssets);
        }
        return result;
    }
}
