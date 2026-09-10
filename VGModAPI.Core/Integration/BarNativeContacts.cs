using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core.Integration;

/// <summary>Inert native presentation. Owned contacts must be excluded from vanilla serialization and sales interaction.</summary>
internal sealed class BarNativeContacts
{
    private readonly ConstructorInfo _constructor;
    private readonly FieldInfo _initialized, _name, _description, _icon, _male;
    private readonly ConditionalWeakTable<object, BarPatronState> _states = new();
    private readonly Func<BarPatronState, object?> _portrait;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal BarNativeContacts(Type salesman, Type patron, Type station, Func<BarPatronState, object?> portrait)
    {
        if (salesman.BaseType != patron) throw new InvalidOperationException("Unsupported native contact inheritance.");
        _constructor = salesman.GetConstructor(new[] { typeof(string), station }) ?? throw new MissingMethodException("Salesman(seed, station)");
        _initialized = patron.GetField("initialized", Fields) ?? throw new MissingFieldException("BarPatron.initialized");
        _name = Field(salesman, "_name", typeof(string));
        _description = Field(salesman, "description", typeof(string));
        _male = Field(salesman, "_isMale", typeof(bool));
        _icon = salesman.GetField("_icon", Fields) ?? throw new MissingFieldException("Salesman._icon");
        if (_initialized.FieldType != typeof(bool) || _icon.FieldType.FullName != "UnityEngine.Sprite")
            throw new InvalidOperationException("Unsupported native contact presentation fields.");
        _portrait = portrait ?? throw new ArgumentNullException(nameof(portrait));
    }

    internal object? Create(BarPatronState state, object station)
    {
        var icon = _portrait(state);
        if (icon == null && state.Portrait == null) return null;
        if (icon != null && !_icon.FieldType.IsInstanceOfType(icon)) return null;
        var contact = _constructor.Invoke(new object[] { state.Seed, station });
        _name.SetValue(contact, state.Name);
        _description.SetValue(contact, state.Description);
        _male.SetValue(contact, state.IsMale);
        _icon.SetValue(contact, icon);
        // Do not invoke Salesman.InitializeData: it creates an unrelated sale and native dialogue.
        _initialized.SetValue(contact, true);
        _states.Add(contact, state);
        return contact;
    }

    internal bool IsOwned(object contact) => _states.TryGetValue(contact, out _);
    internal bool TryGet(object contact, out BarPatronState state) => _states.TryGetValue(contact, out state!);
    private static FieldInfo Field(Type type, string name, Type expected)
    {
        var field = type.GetField(name, Fields);
        return field != null && field.FieldType == expected ? field : throw new MissingFieldException(type.FullName, name);
    }
}
