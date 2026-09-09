using System.Collections.Generic;

// Reflection-shape doubles, not a replacement for the native serializer qualification.
namespace LightJson;
public sealed class JsonObject : IEnumerable<KeyValuePair<string, JsonValue>>
{
    private readonly Dictionary<string, JsonValue> _fields = new();
    public string Text = "{}";
    public System.Func<string>? Render;
    public JsonValue this[string key] { get => _fields.TryGetValue(key, out var value) ? value : new JsonValue(null); set => _fields[key] = value; }
    public bool ContainsKey(string key) => _fields.ContainsKey(key);
    public bool Remove(string key) => _fields.Remove(key);
    public IEnumerator<KeyValuePair<string, JsonValue>> GetEnumerator() => _fields.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => Render?.Invoke() ?? Text;
}
public sealed class JsonValue
{
    private readonly object? _value;
    public JsonValue(object? value) { _value = value; }
    public JsonValue(string? value) : this((object?)value) { }
    // Shape double: preserves supplied text; it does not simulate native JSON parsing.
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonObject> ParseFixtures = new();
    public static JsonValue Parse(string text) => new(ParseFixtures.TryGetValue(text, out var fixture) ? fixture : new JsonObject { Text = text });
    public bool IsJsonObject => _value is JsonObject;
    public bool IsJsonArray => _value is List<JsonValue>;
    public bool IsNull => _value == null;
    public bool IsString => _value is string;
    public bool IsNumber => _value is int or double or float;
    public double AsNumber => System.Convert.ToDouble(_value, System.Globalization.CultureInfo.InvariantCulture);
    public string AsString => (string)_value!;
    public JsonObject AsJsonObject => (JsonObject)_value!;
    public List<JsonValue> AsJsonArray => (List<JsonValue>)_value!;
    public override string ToString() => _value?.ToString() ?? "null";
}
