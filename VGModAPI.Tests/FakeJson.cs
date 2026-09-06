using System.Collections.Generic;

// Reflection-shape doubles, not a replacement for the native serializer qualification.
namespace LightJson;
public sealed class JsonObject
{
    private readonly Dictionary<string, JsonValue> _fields = new();
    public string Text = "{}";
    public JsonValue this[string key] { get => _fields.TryGetValue(key, out var value) ? value : new JsonValue(null); set => _fields[key] = value; }
    public override string ToString() => Text;
}
public sealed class JsonValue
{
    private readonly object? _value;
    public JsonValue(object? value) { _value = value; }
    public bool IsJsonObject => _value is JsonObject;
    public bool IsJsonArray => _value is List<JsonValue>;
    public bool IsNull => _value == null;
    public JsonObject AsJsonObject => (JsonObject)_value!;
    public List<JsonValue> AsJsonArray => (List<JsonValue>)_value!;
    public override string ToString() => _value?.ToString() ?? "null";
}
