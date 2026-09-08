using System;
using System.IO;
using System.Linq;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Stores omitted crew fields beside the same native unit snapshot, avoiding cross-list identity guesses.</summary>
internal sealed class DungeonCrewResumeJson
{
    private const string Key = "vgmodapiCrewExecution";
    private readonly PropertyInfo _object, _item, _isNull, _isString, _string;
    private readonly MethodInfo _convert;
    internal DungeonCrewResumeJson(Assembly assembly)
    {
        var value = assembly.GetType("LightJson.JsonValue", true)!;
        var json = assembly.GetType("LightJson.JsonObject", true)!;
        _object = value.GetProperty("AsJsonObject") ?? throw new MissingMemberException("Json object conversion unavailable.");
        _item = json.GetProperty("Item", new[] { typeof(string) }) ?? throw new MissingMemberException("Json indexer unavailable.");
        _isNull = value.GetProperty("IsNull")!; _isString = value.GetProperty("IsString")!; _string = value.GetProperty("AsString")!;
        if (_object.PropertyType != json || _object.GetMethod == null || _item.PropertyType != value || _item.GetMethod == null || _item.SetMethod == null ||
            _isNull?.PropertyType != typeof(bool) || _isString?.PropertyType != typeof(bool) || _string?.PropertyType != typeof(string))
            throw new MissingMemberException("Unexpected crew JSON schema.");
        _convert = value.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "op_Implicit" && m.ReturnType == value && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string) }));
    }
    internal void Write(object nativeJsonValue, DungeonCrewResumeState state) => WritePayload(nativeJsonValue, Key, DungeonCrewResumeCodec.Encode(state));
    internal void WriteDirectives(object nativeJsonValue, System.Collections.Generic.IEnumerable<DungeonDirectiveState> states) => WritePayload(nativeJsonValue, "vgmodapiDirectives", DungeonDirectiveCodec.Encode(states));
    internal void WriteExecution(object json, DungeonSimulationExecutionState state) => WritePayload(json, "vgmodapiSimulationExecution", state.Encode());
    internal DungeonSimulationExecutionState? ReadExecution(object json)
    {
        var bytes = ReadPayload(json, "vgmodapiSimulationExecution", 16396);
        return bytes == null ? null : DungeonSimulationExecutionState.Decode(bytes);
    }
    private void WritePayload(object nativeJsonValue, string key, byte[] payload)
    {
        var json = _object.GetValue(nativeJsonValue) ?? throw new InvalidDataException("Native crew JSON is not an object.");
        _item.SetValue(json, _convert.Invoke(null, new object[] { Convert.ToBase64String(payload) }), new object[] { key });
    }
    internal DungeonCrewResumeState? Read(object nativeJsonValue)
    {
        var bytes = ReadPayload(nativeJsonValue, Key, 22); return bytes == null ? null : DungeonCrewResumeCodec.Decode(bytes);
    }
    internal System.Collections.Generic.IReadOnlyList<DungeonDirectiveState>? ReadDirectives(object nativeJsonValue)
    {
        var bytes = ReadPayload(nativeJsonValue, "vgmodapiDirectives", OwnerSchemaCodec.MaxPayload);
        return bytes == null ? null : DungeonDirectiveCodec.Decode(bytes);
    }
    private byte[]? ReadPayload(object nativeJsonValue, string key, int maximum)
    {
        var json = _object.GetValue(nativeJsonValue) ?? throw new InvalidDataException("Native crew JSON is not an object.");
        var value = _item.GetValue(json, new object[] { key })!;
        if (_isNull.GetValue(value) is true) return null;
        if (_isString.GetValue(value) is not true) throw new InvalidDataException("Invalid crew supplement type.");
        var encoded = (string)_string.GetValue(value)!;
        if (encoded.Length > ((maximum + 2) / 3) * 4) throw new InvalidDataException("Invalid supplement length.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new InvalidDataException("Invalid supplement encoding.", error); }
        if (bytes.Length > maximum) throw new InvalidDataException("Supplement exceeds payload bound.");
        return bytes;
    }
}
