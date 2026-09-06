using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class MissionJsonBindings
{
    private readonly PropertyInfo _item, _asObject, _asArray, _isObject, _isArray, _isNull;
    private readonly MethodInfo _missionJson;
    internal MissionJsonBindings(Assembly assembly)
    {
        var value = assembly.GetType("LightJson.JsonValue", true)!;
        var obj = assembly.GetType("LightJson.JsonObject", true)!;
        _item = obj.GetProperty("Item", new[] { typeof(string) }) ?? throw new MissingMemberException("JsonObject.Item");
        _asObject = Property(value, "AsJsonObject"); _asArray = Property(value, "AsJsonArray");
        _isObject = Property(value, "IsJsonObject"); _isArray = Property(value, "IsJsonArray"); _isNull = Property(value, "IsNull");
        _missionJson = assembly.GetType(BindingCatalog.Mission, true)!.GetMethod("ToJson", Type.EmptyTypes) ?? throw new MissingMethodException("Mission.ToJson");
    }
    private static PropertyInfo Property(Type owner, string name) => owner.GetProperty(name) ?? throw new MissingMemberException(owner.FullName, name);
    private object Field(object value, string key) => _item.GetValue(value, new object[] { key })!;
    internal string[] SavedFingerprints(object root)
    {
        var playerValue = Field(root, "Player");
        if (!(bool)_isObject.GetValue(playerValue)!) throw new InvalidDataException("No serialized Player object.");
        var player = _asObject.GetValue(playerValue)!; var missions = Field(player, "missions");
        if (!(bool)_isArray.GetValue(missions)!) throw new InvalidDataException("No serialized mission array.");
        var result = new List<string>();
        foreach (var mission in (IEnumerable)_asArray.GetValue(missions)!)
        {
            if (result.Count >= MissionIdentitySnapshot.MaxEntries) throw new InvalidDataException("Too many serialized missions.");
            result.Add(Fingerprint("missions", mission));
        }
        foreach (var key in new[] { "currentBounty", "currentPatrol", "currentIndustry" })
        {
            var mission = Field(player, key);
            if (!(bool)_isNull.GetValue(mission)!) result.Add(Fingerprint(key, mission));
        }
        if (result.Count > MissionIdentitySnapshot.MaxEntries) throw new InvalidDataException("Too many serialized missions.");
        return result.ToArray();
    }
    internal string CurrentFingerprint(string container, object mission) => Fingerprint(container, _missionJson.Invoke(mission, null)!);
    private string Fingerprint(string container, object value)
    {
        if (!(bool)_isObject.GetValue(value)!) throw new InvalidDataException("Invalid serialized mission object.");
        string text = value.ToString()!;
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("Mission serialization exceeds identity inspection limit.");
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(container + "\0" + text))).Replace("-", "").ToLowerInvariant();
    }
}
