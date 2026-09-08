using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core.Integration;

/// <summary>Exact-reference roster swap. Contact construction is supplied by the inspected native binding.</summary>
internal sealed class BarNativeWorld : IBarRosterWorld
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly BarStationSource _station;
    private readonly Func<object, bool> _owned;
    private readonly Func<BarPatronState, object, object?> _create;
    private readonly FieldInfo _bar, _guid, _patrons, _seat;
    private readonly Type _patronType;
    private readonly int _capacity;
    private readonly ConditionalWeakTable<object, RetainedRoster> _retained = new();

    private sealed class RetainedRoster
    {
        internal readonly IList Applied;
        internal readonly object[] Entries, Vanilla;
        internal RetainedRoster(IList applied, object[] entries, object[] vanilla)
        { Applied = applied; Entries = entries; Vanilla = vanilla; }
        internal bool Matches(object? list) => ReferenceEquals(list, Applied) && Applied.Count == Entries.Length
            && Entries.Select((entry, index) => ReferenceEquals(entry, Applied[index])).All(value => value);
    }

    internal object[]? RetainedVanilla(object bar)
    {
        return _retained.TryGetValue(bar, out var retained) && retained.Matches(_patrons.GetValue(bar))
            ? retained.Vanilla.ToArray() : null;
    }

    internal BarNativeWorld(Type stationType, Type barType, Type patronType, BarStationSource station,
        Func<object, bool> owned, Func<BarPatronState, object, object?> create, int capacity)
    {
        _bar = stationType.GetField("bar", Fields) ?? throw new MissingFieldException("station.bar");
        _guid = stationType.GetField("guid", Fields) ?? throw new MissingFieldException("station.guid");
        _patrons = barType.GetField("availablePatrons", Fields) ?? throw new MissingFieldException("bar.availablePatrons");
        _seat = patronType.GetField("seat", Fields) ?? throw new MissingFieldException("BarPatron.seat");
        if (_seat.FieldType != typeof(int) || _bar.FieldType != barType || _guid.FieldType != typeof(string)
            || _patrons.FieldType != typeof(List<>).MakeGenericType(patronType)) throw new InvalidOperationException("Unsupported native bar shape.");
        if (capacity < 1 || capacity > 32) throw new ArgumentOutOfRangeException(nameof(capacity));
        _station = station; _owned = owned; _create = create; _patronType = patronType; _capacity = capacity;
    }

    private sealed class CaptureToken
    {
        internal readonly BarNativeWorld Owner;
        internal readonly object Station, Bar;
        internal readonly string Identity;
        internal readonly IList List;
        internal readonly object[] Entries;
        internal readonly int[] Seats;
        internal object[] Vanilla = Array.Empty<object>();
        internal int[] VanillaSeats = Array.Empty<int>();
        internal CaptureToken(BarNativeWorld owner, object station, object bar, IList list, object[] entries, int[] seats, string identity)
        { Owner = owner; Station = station; Bar = bar; List = list; Entries = entries; Seats = seats; Identity = identity; }
    }

    public BarRosterSnapshot? Capture(string station)
    {
        var current = _station.Read();
        if (current == null || (string?)_guid.GetValue(current) != station) return null;
        var bar = _bar.GetValue(current);
        if (bar == null || _patrons.GetValue(bar) is not IList list || list.Count > _capacity) return null;
        var entries = list.Cast<object>().ToArray();
        if (entries.Any(entry => entry == null || !_patronType.IsInstanceOfType(entry))) return null;
        var token = new CaptureToken(this, current, bar, list, entries, entries.Select(entry => (int)_seat.GetValue(entry)!).ToArray(), station);
        var vanilla = RetainedVanilla(bar) ?? entries.Where(entry => !_owned(entry)).ToArray();
        token.Vanilla = vanilla;
        token.VanillaSeats = vanilla.Select(entry => (int)_seat.GetValue(entry)!).ToArray();
        if (!Stable(token)) return null;
        return new BarRosterSnapshot(token, vanilla, _capacity);
    }

    public object? CreateContact(BarPatronState state)
    {
        var station = _station.Read();
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
        var occupied = new HashSet<int>();
        var contacts = new List<object>();
        foreach (var patron in copy)
        {
            if (snapshot.VanillaPatrons.Any(original => ReferenceEquals(original, patron)))
            {
                int seat = (int)_seat.GetValue(patron)!;
                if (seat < 1 || seat > _capacity || !occupied.Add(seat)) return false;
            }
            else
            {
                if (token.Entries.Any(original => ReferenceEquals(original, patron)) || !_owned(patron)) return false;
                contacts.Add(patron);
            }
        }
        foreach (var contact in contacts)
        {
            int seat = Enumerable.Range(1, _capacity).First(index => !occupied.Contains(index));
            _seat.SetValue(contact, seat);
            occupied.Add(seat);
        }
        var replacement = (IList)Activator.CreateInstance(_patrons.FieldType)!;
        foreach (var patron in copy) replacement.Add(patron);
        if (!Stable(token) || !stillValid() || !Stable(token)) return false;
        var retained = new RetainedRoster(replacement, copy, token.Vanilla);
        _patrons.SetValue(token.Bar, replacement);
        _retained.Remove(token.Bar);
        _retained.Add(token.Bar, retained);
        return true;
    }

    private bool Stable(CaptureToken token)
    {
        if (!ReferenceEquals(_station.Read(), token.Station) || (string?)_guid.GetValue(token.Station) != token.Identity
            || !ReferenceEquals(_bar.GetValue(token.Station), token.Bar)
            || !ReferenceEquals(_patrons.GetValue(token.Bar), token.List) || token.List.Count != token.Entries.Length) return false;
        for (int i = 0; i < token.Entries.Length; i++) if (!ReferenceEquals(token.List[i], token.Entries[i]) || (int)_seat.GetValue(token.Entries[i])! != token.Seats[i]) return false;
        for (int i = 0; i < token.Vanilla.Length; i++) if ((int)_seat.GetValue(token.Vanilla[i])! != token.VanillaSeats[i]) return false;
        return true;
    }
}
