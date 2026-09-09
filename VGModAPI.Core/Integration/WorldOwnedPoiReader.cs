using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace VGModAPI.Core.Integration;

/// <summary>Owned-only reconstruction using an already authenticated JSON object, without string-based Create dispatch.</summary>
internal sealed class WorldOwnedPoiReader
{
    private readonly ConstructorInfo _constructor;
    private readonly MethodInfo _load, _optional, _string;
    private readonly PropertyInfo _item, _number, _storeLastX;
    private readonly FieldInfo _danger, _hazards, _visited, _lastX;
    private readonly Type _json;
    internal WorldOwnedPoiReader(Assembly game) : this(game.GetType("Source.Galaxy.POI.Combat", true)!,
        game.GetType("Source.Galaxy.MapPointOfInterest", true)!, game.GetType("LightJson.JsonObject", true)!, game.GetType("LightJson.JsonValue", true)!) { }
    internal WorldOwnedPoiReader(Type combat, Type poi, Type json, Type value)
    {
        _json = json;
        _constructor = combat.GetConstructor(Type.EmptyTypes) ?? throw new MissingMethodException("Combat constructor");
        _load = combat.GetMethod("LoadFromJson", BindingFlags.Public | BindingFlags.Instance, null, new[] { json }, null)
            ?? throw new MissingMethodException("Combat.LoadFromJson");
        _optional = poi.GetMethod("LoadOptionalPoiData", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { poi, json }, null)
            ?? throw new MissingMethodException("MapPointOfInterest.LoadOptionalPoiData");
        _item = json.GetProperty("Item", new[] { typeof(string) }) ?? throw new MissingMemberException("JsonObject.Item");
        _string = value.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(method => method.Name == "op_Implicit" &&
            method.ReturnType == typeof(string) && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == value);
        _number = value.GetProperty("AsNumber") ?? throw new MissingMemberException("JsonValue.AsNumber");
        _storeLastX = poi.GetProperty("storeLastX") ?? throw new MissingMemberException("MapPointOfInterest.storeLastX");
        _danger = Field(poi, "dangerLevel", typeof(string)); _hazards = Field(poi, "hazardsDescription", typeof(string));
        _visited = Field(poi, "lastVisitedTime", typeof(float)); _lastX = Field(poi, "lastVisitedX", typeof(float));
    }
    private static FieldInfo Field(Type type, string name, Type expected)
    {
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field == null || field.FieldType != expected || field.IsInitOnly) throw new MissingFieldException(type.FullName, name);
        return field;
    }
    internal object Read(object json, Action requireAdmission)
    {
        if (!_json.IsInstanceOfType(json)) throw new InvalidDataException("Expected authenticated owned POI JSON.");
        if (requireAdmission == null) throw new ArgumentNullException(nameof(requireAdmission));
        object Value(string key) => _item.GetValue(json, new object[] { key })!;
        try
        {
            requireAdmission();
            var result = _constructor.Invoke(Array.Empty<object>());
            requireAdmission();
            _load.Invoke(result, new[] { json });
            requireAdmission();
            _danger.SetValue(result, _string.Invoke(null, new[] { Value("dangerLevel") }));
            _hazards.SetValue(result, _string.Invoke(null, new[] { Value("hazardsDescription") }));
            _visited.SetValue(result, (float)(double)_number.GetValue(Value("lastVisitedTime"))!);
            if ((bool)_storeLastX.GetValue(result)!) _lastX.SetValue(result, (float)(double)_number.GetValue(Value("lastVisitedX"))!);
            requireAdmission();
            _optional.Invoke(null, new[] { result, json });
            requireAdmission();
            return result;
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
