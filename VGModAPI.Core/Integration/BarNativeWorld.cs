using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>Exact-reference roster swap. Contact construction is supplied by the inspected native binding.</summary>
internal sealed class BarNativeWorld : IBarRosterWorld
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Func<object?> _station;
    private readonly Func<object, bool> _owned;
    private readonly Func<BarPatronState, object, object?> _create;
    private readonly FieldInfo _bar, _guid, _patrons;
    private readonly Type _patronType;
    private readonly int _capacity;

    internal BarNativeWorld(Type stationType, Type barType, Type patronType, Func<object?> station,
        Func<object, bool> owned, Func<BarPatronState, object, object?> create, int capacity)
    {
        _bar = stationType.GetField("bar", Fields) ?? throw new MissingFieldException("station.bar");
        _guid = stationType.GetField("guid", Fields) ?? throw new MissingFieldException("station.guid");
        _patrons = barType.GetField("availablePatrons", Fields) ?? throw new MissingFieldException("bar.availablePatrons");
        if (_bar.FieldType != barType || _guid.FieldType != typeof(string)
            || _patrons.FieldType != typeof(List<>).MakeGenericType(patronType)) throw new InvalidOperationException("Unsupported native bar shape.");
        if (capacity < 1 || capacity > 32) throw new ArgumentOutOfRangeException(nameof(capacity));
        _station = station; _owned = owned; _create = create; _patronType = patronType; _capacity = capacity;
    }

    private sealed class CaptureToken
    {
        internal readonly BarNativeWorld Owner;
        internal readonly object Station, Bar;
        internal readonly IList List;
        internal readonly object[] Entries;
        internal CaptureToken(BarNativeWorld owner, object station, object bar, IList list, object[] entries)
        { Owner = owner; Station = station; Bar = bar; List = list; Entries = entries; }
    }

    public BarRosterSnapshot? Capture(string station)
    {
        var current = _station();
        if (current == null || (string?)_guid.GetValue(current) != station) return null;
        var bar = _bar.GetValue(current);
        if (bar == null || _patrons.GetValue(bar) is not IList list || list.Count > _capacity) return null;
        var entries = list.Cast<object>().ToArray();
        if (entries.Any(entry => entry == null || !_patronType.IsInstanceOfType(entry))) return null;
        var token = new CaptureToken(this, current, bar, list, entries);
        var vanilla = entries.Where(entry => !_owned(entry)).ToArray();
        if (!Stable(token)) return null;
        return new BarRosterSnapshot(token, vanilla, _capacity);
    }

    public object? CreateContact(BarPatronState state)
    {
        var station = _station();
        if (station == null || (string?)_guid.GetValue(station) != state.Station) return null;
        var contact = _create(state, station);
        return contact != null && _patronType.IsInstanceOfType(contact) ? contact : null;
    }

    public bool Apply(BarRosterSnapshot snapshot, IReadOnlyList<object> patrons, Func<bool> stillValid)
    {
        if (snapshot.Token is not CaptureToken token || !ReferenceEquals(token.Owner, this) || patrons.Count > _capacity) return false;
        var copy = patrons.ToArray();
        if (copy.Any(patron => patron == null || !_patronType.IsInstanceOfType(patron))) return false;
        for (int i = 0; i < copy.Length; i++)
            for (int j = 0; j < i; j++) if (ReferenceEquals(copy[i], copy[j])) return false;
        var replacement = (IList)Activator.CreateInstance(_patrons.FieldType)!;
        foreach (var patron in copy) replacement.Add(patron);
        if (!Stable(token) || !stillValid() || !Stable(token)) return false;
        _patrons.SetValue(token.Bar, replacement);
        return true;
    }

    private bool Stable(CaptureToken token)
    {
        if (!ReferenceEquals(_station(), token.Station) || !ReferenceEquals(_bar.GetValue(token.Station), token.Bar)
            || !ReferenceEquals(_patrons.GetValue(token.Bar), token.List) || token.List.Count != token.Entries.Length) return false;
        for (int i = 0; i < token.Entries.Length; i++) if (!ReferenceEquals(token.List[i], token.Entries[i])) return false;
        return true;
    }
}
