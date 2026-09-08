using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>API-required native load barrier. It is not authorization or a save conversion/uninstall facility.</summary>
internal sealed class WorldSaveFormat
{
    internal const string Marker = "99.99.99.99";
    internal const string OriginalVersion = "VGModAPIWorldOriginalVersion";
    private readonly Type _rootType;
    private readonly PropertyInfo _item, _isString, _asString;
    private readonly MethodInfo _contains, _remove;
    private readonly ConstructorInfo _string;
    internal WorldSaveFormat(Assembly assembly)
    {
        _rootType = assembly.GetType("LightJson.JsonObject", true)!;
        var value = assembly.GetType("LightJson.JsonValue", true)!;
        _item = _rootType.GetProperty("Item", new[] { typeof(string) })!;
        _contains = _rootType.GetMethod("ContainsKey", new[] { typeof(string) })!;
        _remove = _rootType.GetMethod("Remove", new[] { typeof(string) })!;
        _isString = value.GetProperty("IsString")!; _asString = value.GetProperty("AsString")!;
        _string = value.GetConstructor(new[] { typeof(string) }) ?? throw new MissingMethodException("JsonValue(string)");
        if (_item == null || _contains == null || _remove == null || _isString == null || _asString == null)
            throw new MissingMemberException("World save format JSON bindings unavailable.");
    }
    private string Text(object root, string key)
    {
        var value = _item.GetValue(root, new object[] { key })!;
        if (!(bool)_isString.GetValue(value)!) throw new InvalidDataException("World save version must be a string.");
        return (string)_asString.GetValue(value)!;
    }
    private bool Has(object root, string key) => (bool)_contains.Invoke(root, new object[] { key })!;
    private void Set(object root, string key, string value) => _item.SetValue(root, _string.Invoke(new object[] { value }), new object[] { key });
    private void Root(object root)
    { if (root == null || root.GetType() != _rootType) throw new InvalidDataException("Exact native JSON root required."); }

    internal void Seal(object root, bool owned)
    {
        Root(root);
        string version = Text(root, "Version");
        if (version == Marker || Has(root, OriginalVersion)) throw new InvalidDataException("Preexisting world format marker requires a fresh native snapshot.");
        ValidateVersion(version);
        if (!owned) return;
        var before = OtherFields(root);
        Set(root, OriginalVersion, version); Set(root, "Version", Marker);
        RequireSame(before, OtherFields(root));
    }
    // The caller must verify the complete sealed native bytes, generation and definitions first.
    internal void UnsealVerified(object root, bool owned)
    {
        Root(root);
        string version = Text(root, "Version");
        if (!owned)
        {
            if (version == Marker || Has(root, OriginalVersion)) throw new InvalidDataException("World format marker conflicts with empty inventory.");
            return;
        }
        if (version != Marker || !Has(root, OriginalVersion)) throw new InvalidDataException("Owned world save lacks its API-required barrier.");
        string original = Text(root, OriginalVersion); ValidateVersion(original);
        var before = OtherFields(root);
        Set(root, "Version", original);
        if (!(bool)_remove.Invoke(root, new object[] { OriginalVersion })!) throw new InvalidDataException("World format field removal failed.");
        RequireSame(before, OtherFields(root));
        // Do not bypass vanilla loadedVersion/IsFuture: a genuinely future original still refuses.
    }
    private static void ValidateVersion(string version)
    {
        if (version == Marker) throw new InvalidDataException("Nested world version marker.");
        var parts = version.Split('.');
        if (parts.Length < 2 || parts.Length > 4) throw new InvalidDataException("Invalid original game version.");
        foreach (var part in parts)
        {
            if (part.Length < 1 || part.Length > 2) throw new InvalidDataException("Invalid original game version segment.");
            foreach (char c in part) if (c < '0' || c > '9') throw new InvalidDataException("Invalid original game version digit.");
        }
    }
    private static Dictionary<string, string> OtherFields(object root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal); long length = 0; int count = 0;
        foreach (var entry in (IEnumerable)root)
        {
            if (++count > 1024) throw new InvalidDataException("World root field limit exceeded.");
            var type = entry.GetType(); var key = (string)type.GetProperty("Key")!.GetValue(entry)!;
            if (key == "Version" || key == OriginalVersion) continue;
            string text = type.GetProperty("Value")!.GetValue(entry)!.ToString() ?? throw new InvalidDataException("Missing root field text.");
            length += text.Length;
            if (length > WorldLoadBytes.MaxDecodedBytes) throw new InvalidDataException("World root content limit exceeded.");
            result.Add(key, text);
        }
        return result;
    }
    private static void RequireSame(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        if (before.Count != after.Count) throw new InvalidDataException("World format transformation changed root fields.");
        foreach (var pair in before) if (!after.TryGetValue(pair.Key, out var text) || text != pair.Value)
            throw new InvalidDataException("World format transformation changed unrelated native data.");
    }
}
