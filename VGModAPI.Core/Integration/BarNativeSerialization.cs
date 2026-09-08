using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>Serializes vanilla roster data without temporarily mutating the live roster.</summary>
internal sealed class BarNativeSerialization
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly FieldInfo _patrons, _time, _seed;
    private readonly MethodInfo _serialize, _addItem, _addProperty;
    private readonly ConstructorInfo _array, _object, _arrayValue, _objectValue, _stringValue;
    private readonly BarNativeContacts _contacts;
    private readonly BarNativeWorld? _world;

    internal BarNativeSerialization(Type bar, Type patron, Type value, Type jsonObject, Type jsonArray, BarNativeContacts contacts, BarNativeWorld? world = null)
    {
        _patrons = bar.GetField("availablePatrons", Fields) ?? throw new MissingFieldException("Bar.availablePatrons");
        _time = bar.GetField("lastUpdateTime", Fields) ?? throw new MissingFieldException("Bar.lastUpdateTime");
        _seed = bar.GetField("nextUpdateSeed", Fields) ?? throw new MissingFieldException("Bar.nextUpdateSeed");
        if (_time.FieldType != typeof(long) || _seed.FieldType != typeof(string)) throw new InvalidOperationException("Unsupported bar serialization fields.");
        _serialize = patron.GetMethod("ToJson", Type.EmptyTypes) ?? throw new MissingMethodException("BarPatron.ToJson");
        if (_serialize.ReturnType != value) throw new InvalidOperationException("Unsupported patron JSON result.");
        _array = Constructor(jsonArray); _object = Constructor(jsonObject);
        _arrayValue = Constructor(value, jsonArray); _objectValue = Constructor(value, jsonObject); _stringValue = Constructor(value, typeof(string));
        _addItem = jsonArray.GetMethod("Add", new[] { value }) ?? throw new MissingMethodException("JsonArray.Add");
        _addProperty = jsonObject.GetMethod("Add", new[] { typeof(string), value }) ?? throw new MissingMethodException("JsonObject.Add");
        _contacts = contacts;
        _world = world;
    }

    internal bool TrySerialize(object bar, Func<bool> ready, out object? result)
    {
        result = null;
        if (_patrons.GetValue(bar) is not IList list || list.Count > 32) throw new InvalidOperationException("Unbounded bar roster serialization.");
        var entries = list.Cast<object>().ToArray();
        var owned = entries.Select(entry => entry != null && _contacts.IsOwned(entry)).ToArray();
        var retained = _world?.RetainedVanilla(bar);
        bool hiddenVanilla = retained != null && retained.Any(patron => !entries.Any(entry => ReferenceEquals(entry, patron)));
        if (!owned.Any(value => value) && !hiddenVanilla) return false;
        var native = retained ?? entries.Where((_, index) => !owned[index]).ToArray();
        var time = (long)_time.GetValue(bar)!;
        var seed = (string?)_seed.GetValue(bar);
        bool Stable()
        {
            if (!ReferenceEquals(_patrons.GetValue(bar), list) || list.Count != entries.Length
                || (long)_time.GetValue(bar)! != time || (string?)_seed.GetValue(bar) != seed) return false;
            for (int index = 0; index < entries.Length; index++) if (!ReferenceEquals(list[index], entries[index])) return false;
            return true;
        }
        if (!ready() || !Stable()) throw new InvalidOperationException("Owned patron state is not safe to serialize.");
        var array = _array.Invoke(Array.Empty<object>());
        foreach (var patron in native)
        {
            if (patron == null) throw new InvalidOperationException("Null native patron.");
            var serialized = _serialize.Invoke(patron, null);
            _addItem.Invoke(array, new[] { serialized });
        }
        var obj = _object.Invoke(Array.Empty<object>());
        _addProperty.Invoke(obj, new[] { "availablePatrons", _arrayValue.Invoke(new[] { array }) });
        _addProperty.Invoke(obj, new[] { "lastUpdateTime", _stringValue.Invoke(new object[] { time.ToString() }) });
        _addProperty.Invoke(obj, new[] { "nextUpdateSeed", _stringValue.Invoke(new object?[] { seed }) });
        var value = _objectValue.Invoke(new[] { obj });
        if (!ready() || !Stable()) throw new InvalidOperationException("Bar roster changed during serialization.");
        result = value;
        return true;
    }

    private static ConstructorInfo Constructor(Type type, params Type[] arguments) => type.GetConstructor(arguments)
        ?? throw new MissingMethodException(type.FullName, ".ctor");
}
