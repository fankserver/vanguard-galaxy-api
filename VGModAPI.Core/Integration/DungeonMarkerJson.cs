using System;
using System.Linq;
using System.Reflection;

namespace VGModAPI.Runtime;

/// <summary>The native location carries only occurrence identity; API-owned data stays in its provider envelope.</summary>
internal sealed class DungeonMarkerJson
{
    private readonly string Key;
    private readonly PropertyInfo _item, _string, _isString;
    private readonly MethodInfo _convert;
    internal DungeonMarkerJson(Assembly assembly, string key = "vgmodapiDungeonOccurrence")
    {
        Key = key;
        var json = assembly.GetType("LightJson.JsonObject", true)!; var value = assembly.GetType("LightJson.JsonValue", true)!;
        _item = json.GetProperty("Item", new[] { typeof(string) }) ?? throw new MissingMemberException(json.FullName, "Item");
        if (_item.PropertyType != value || _item.GetMethod == null || _item.SetMethod == null) throw new MissingMemberException("Writable JsonObject string indexer required.");
        _string = value.GetProperty("AsString") ?? throw new MissingMemberException(value.FullName, "AsString");
        _isString = value.GetProperty("IsString") ?? throw new MissingMemberException(value.FullName, "IsString");
        if (_string.PropertyType != typeof(string) || _isString.PropertyType != typeof(bool) || _string.GetMethod == null || _isString.GetMethod == null) throw new MissingMemberException("Unexpected JsonValue string properties.");
        _convert = value.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "op_Implicit" && m.ReturnType == value && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string) }));
    }
    internal Guid? Read(object json)
    {
        var value = _item.GetValue(json, new object[] { Key })!;
        if (_isString.GetValue(value) is not true) return null;
        return Guid.TryParseExact((string?)_string.GetValue(value), "D", out var id) && id != Guid.Empty ? id : null;
    }
    internal Guid? ReadStrict(object json)
    {
        var value = _item.GetValue(json, new object[] { Key })!;
        var isNull = value.GetType().GetProperty("IsNull") ?? throw new MissingMemberException("Json null predicate unavailable.");
        if (isNull.GetValue(value) is true) return null;
        return Read(json) ?? throw new System.IO.InvalidDataException("Invalid persistent dungeon identity marker.");
    }
    internal void Write(object json, Guid occurrence)
    {
        if (occurrence == Guid.Empty) throw new ArgumentException("Occurrence identity required.");
        var value = _convert.Invoke(null, new object[] { occurrence.ToString("D") });
        _item.SetValue(json, value, new object[] { Key });
    }
}
