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
    private readonly FieldInfo _bar, _guid, _patrons, _seat, _updateTime;
    private readonly Type _patronType;
    private readonly PropertyInfo _seed;
    private readonly int _capacity;
    private BarHostHealth? _health;
    private bool _stopped;
    private readonly ConditionalWeakTable<object, object> _knownBars = new();
    private readonly List<WeakReference<object>> _trackedBars = new();
    internal void AttachHealth(BarHostHealth health)
    {
        if (_health != null) throw new InvalidOperationException("A native bar world already has a host.");
        _health = health ?? throw new ArgumentNullException(nameof(health));
    }
    private readonly ConditionalWeakTable<object, RetainedRoster> _retained = new();
    private readonly ConditionalWeakTable<object, RefreshToken> _refreshes = new();
    private readonly ConditionalWeakTable<object, object> _refreshEpochs = new();

    internal object CaptureRefreshEpoch(object bar)
    {
        if (_refreshes.TryGetValue(bar, out _)) throw new InvalidOperationException("Native bar refresh is in progress.");
        return _refreshEpochs.GetValue(bar, _ => new object());
    }

    internal bool IsRefreshEpochCurrent(object bar, object epoch) => !_refreshes.TryGetValue(bar, out _)
        && _refreshEpochs.TryGetValue(bar, out var current) && ReferenceEquals(epoch, current);

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
        if (_refreshes.TryGetValue(bar, out _)) throw new InvalidOperationException("Native bar refresh is in progress.");
        if (!_retained.TryGetValue(bar, out var retained)) return null;
        if (!retained.Matches(_patrons.GetValue(bar)))
            throw new InvalidOperationException("Retained vanilla roster is uncertain; a verified native refresh is required.");
        return retained.Vanilla.ToArray();
    }

    internal sealed class RefreshToken
    {
        internal readonly BarNativeWorld Owner;
        internal readonly object Bar;
        internal readonly long Time;
        internal bool Completed;
        internal readonly RefreshToken? Parent;
        internal RefreshToken(BarNativeWorld owner, object bar, long time, RefreshToken? parent)
        { Owner = owner; Bar = bar; Time = time; Parent = parent; }
    }

    internal RefreshToken BeginNativeRefresh(object bar)
    {
        _refreshEpochs.Remove(bar);
        _refreshEpochs.Add(bar, new object());
        _refreshes.TryGetValue(bar, out var parent);
        var token = new RefreshToken(this, bar, (long)_updateTime.GetValue(bar)!, parent);
        _refreshes.Remove(bar);
        _refreshes.Add(bar, token);
        return token;
    }

    // Call only from the inspected CheckUpdatePatrons boundary with Harmony's original-run
    // evidence and successful completion. The final native timestamp write proves regeneration,
    // unlike arbitrary edits to the list by another postfix. A daily no-op retains the baseline.
    internal bool CompleteNativeRefresh(RefreshToken token, bool originalRan, bool succeeded)
    {
        if (!ReferenceEquals(token.Owner, this) || token.Completed
            || !_refreshes.TryGetValue(token.Bar, out var current) || !ReferenceEquals(current, token)) return false;
        token.Completed = true;
        _refreshes.Remove(token.Bar);
        if (token.Parent != null) { _refreshes.Add(token.Bar, token.Parent); return false; }
        if (!originalRan || !succeeded || (long)_updateTime.GetValue(token.Bar)! == token.Time) return false;
        _retained.Remove(token.Bar);
        return true;
    }

    internal BarNativeWorld(Type stationType, Type barType, Type patronType, BarStationSource station,
        Func<object, bool> owned, Func<BarPatronState, object, object?> create, int capacity)
    {
        _bar = stationType.GetField("bar", Fields) ?? throw new MissingFieldException("station.bar");
        _guid = stationType.GetField("guid", Fields) ?? GuidBackingField(stationType);
        _patrons = barType.GetField("availablePatrons", Fields) ?? throw new MissingFieldException("bar.availablePatrons");
        _seat = patronType.GetField("seat", Fields) ?? throw new MissingFieldException("BarPatron.seat");
        _updateTime = barType.GetField("lastUpdateTime", Fields) ?? throw new MissingFieldException("Bar.lastUpdateTime");
        _seed = patronType.GetProperty("seed") ?? throw new MissingMemberException("BarPatron.seed");
        if (_seed.PropertyType != typeof(string) || _updateTime.FieldType != typeof(long) || _seat.FieldType != typeof(int) || _bar.FieldType != barType || _guid.FieldType != typeof(string)
            || _patrons.FieldType != typeof(List<>).MakeGenericType(patronType)) throw new InvalidOperationException("Unsupported native bar shape.");
        if (capacity < 1 || capacity > 32) throw new ArgumentOutOfRangeException(nameof(capacity));
        _station = station; _owned = owned; _create = create; _patronType = patronType; _capacity = capacity;
    }

    private static FieldInfo GuidBackingField(Type stationType)
    {
        var property = stationType.GetProperty("guid", Fields);
        var field = property?.DeclaringType?.GetField("<guid>k__BackingField", Fields | BindingFlags.DeclaredOnly);
        if (property?.PropertyType != typeof(string) || property.GetMethod == null || property.GetMethod.IsVirtual
            || field?.FieldType != typeof(string) || !field.IsDefined(typeof(CompilerGeneratedAttribute), false)
            || !property.GetMethod.IsDefined(typeof(CompilerGeneratedAttribute), false))
            throw new MissingFieldException("station.guid must be a field or inspected nonvirtual auto-property.");
        return field;
    }

    private sealed class CaptureToken
    {
        internal readonly BarNativeWorld Owner;
        internal readonly object Station, Bar;
        internal readonly string Identity;
        internal readonly object RefreshEpoch;
        internal readonly IList List;
        internal readonly object[] Entries;
        internal readonly int[] Seats;
        internal object[] Vanilla = Array.Empty<object>();
        internal int[] VanillaSeats = Array.Empty<int>();
        internal CaptureToken(BarNativeWorld owner, object station, object bar, IList list, object[] entries, int[] seats, string identity, object refreshEpoch)
        { Owner = owner; Station = station; Bar = bar; List = list; Entries = entries; Seats = seats; Identity = identity; RefreshEpoch = refreshEpoch; }
    }

    public BarRosterSnapshot? Capture(string station)
    {
        var current = _station.Read();
        if (current == null || (string?)_guid.GetValue(current) != station) return null;
        var bar = _bar.GetValue(current);
        if (bar == null || _patrons.GetValue(bar) is not IList list || list.Count > _capacity) return null;
        var refreshEpoch = CaptureRefreshEpoch(bar);
        var entries = list.Cast<object>().ToArray();
        if (entries.Any(entry => entry == null || !_patronType.IsInstanceOfType(entry))) return null;
        var token = new CaptureToken(this, current, bar, list, entries, entries.Select(entry => (int)_seat.GetValue(entry)!).ToArray(), station, refreshEpoch);
        var vanilla = RetainedVanilla(bar) ?? entries.Where(entry => !_owned(entry)).ToArray();
        token.Vanilla = vanilla;
        token.VanillaSeats = vanilla.Select(entry => (int)_seat.GetValue(entry)!).ToArray();
        if (!Stable(token)) return null;
        return new BarRosterSnapshot(token, vanilla, _capacity);
    }

    internal sealed class Observation
    {
        private readonly BarNativeWorld _world;
        private readonly CaptureToken _token;
        internal BarRosterFinalized Snapshot { get; }
        internal Observation(BarNativeWorld world, object token, BarRosterFinalized snapshot)
        { _world = world; _token = (CaptureToken)token; Snapshot = snapshot; }
        internal bool IsCurrent => _world.Stable(_token);
    }

    internal Observation? Observe(BarRosterPlan plan, BarNativeContacts contacts)
    {
        var captured = Capture(plan.Station);
        if (captured == null) return null;
        var token = (CaptureToken)captured.Token;
        var members = token.Entries.Select(patron => new BarRosterMember(
            contacts.TryGet(patron, out var state) ? state.Id : (BarPatronId?)null,
            patron.GetType().Name, (string?)_seed.GetValue(patron) ?? "", (int)_seat.GetValue(patron)!)).ToArray();
        var snapshot = new BarRosterFinalized(plan.Session, plan.Station, members,
            plan.Policy.Denied.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal));
        var observation = new Observation(this, token, snapshot);
        return observation.IsCurrent ? observation : null;
    }

    internal sealed class ContactAdmission
    {
        private readonly BarNativeWorld _world;
        private readonly CaptureToken _token;
        private readonly object _contact;
        internal ContactAdmission(BarNativeWorld world, object token, object contact)
        { _world = world; _token = (CaptureToken)token; _contact = contact; }
        internal bool IsCurrent => _token.Entries.Any(entry => ReferenceEquals(entry, _contact)) && _world.Stable(_token);
    }

    internal string? CurrentStationId(object bar)
    {
        var station = _station.Read();
        return station != null && ReferenceEquals(_bar.GetValue(station), bar) ? (string?)_guid.GetValue(station) : null;
    }

    internal object[] CurrentRoster(object bar)
    {
        if (CurrentStationId(bar) == null || _patrons.GetValue(bar) is not IList list || list.Count > _capacity)
            return Array.Empty<object>();
        return list.Cast<object>().ToArray();
    }

    internal ContactAdmission? CaptureContact(object contact)
    {
        var station = _station.Read();
        if (station == null || _guid.GetValue(station) is not string identity) return null;
        var snapshot = Capture(identity);
        if (snapshot == null) return null;
        var admission = new ContactAdmission(this, snapshot.Token, contact);
        return admission.IsCurrent ? admission : null;
    }

    public object? CreateContact(BarPatronState state)
    {
        if (_stopped) return null;
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
        if (!Stable(token) || !stillValid() || !Stable(token) || _refreshes.TryGetValue(token.Bar, out _)) return false;
        if (!_knownBars.TryGetValue(token.Bar, out _))
        {
            _trackedBars.RemoveAll(reference => !reference.TryGetTarget(out _));
            if (_trackedBars.Count >= 4096) return false;
            _knownBars.Add(token.Bar, new object());
            _trackedBars.Add(new WeakReference<object>(token.Bar));
        }
        var retained = new RetainedRoster(replacement, copy, token.Vanilla);
        _patrons.SetValue(token.Bar, replacement);
        _retained.Remove(token.Bar);
        _retained.Add(token.Bar, retained);
        return true;
    }

    // Restore roster storage during shutdown. Contact guards must still outlive stale UI
    // references; a false result also requires retaining bar serialization guards.
    // Validation and allocation finish for every tracked bar before any roster is restored.
    internal bool StopAndRestore()
    {
        _stopped = true;
        var replacements = new List<(object Bar, IList List)>();
        foreach (var reference in _trackedBars)
        {
            if (!reference.TryGetTarget(out var bar) || !_retained.TryGetValue(bar, out var retained)) continue;
            if (_refreshes.TryGetValue(bar, out _) || !retained.Matches(_patrons.GetValue(bar))) return false;
            var list = (IList)Activator.CreateInstance(_patrons.FieldType)!;
            foreach (var patron in retained.Vanilla) list.Add(patron);
            replacements.Add((bar, list));
        }
        foreach (var replacement in replacements)
        {
            _patrons.SetValue(replacement.Bar, replacement.List);
            _retained.Remove(replacement.Bar);
        }
        return true;
    }

    private bool Stable(CaptureToken token)
    {
        if (_stopped || _health?.IsHealthy == false || !IsRefreshEpochCurrent(token.Bar, token.RefreshEpoch) || !ReferenceEquals(_station.Read(), token.Station) || (string?)_guid.GetValue(token.Station) != token.Identity
            || !ReferenceEquals(_bar.GetValue(token.Station), token.Bar)
            || !ReferenceEquals(_patrons.GetValue(token.Bar), token.List) || token.List.Count != token.Entries.Length) return false;
        for (int i = 0; i < token.Entries.Length; i++) if (!ReferenceEquals(token.List[i], token.Entries[i]) || (int)_seat.GetValue(token.Entries[i])! != token.Seats[i]) return false;
        for (int i = 0; i < token.Vanilla.Length; i++) if ((int)_seat.GetValue(token.Vanilla[i])! != token.VanillaSeats[i]) return false;
        return true;
    }
}
