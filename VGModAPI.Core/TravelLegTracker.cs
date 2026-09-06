using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

// Pure evidence reducer. The adapter must supply verified departure/readiness facts;
// calling a route method or creating an iterator is not such evidence.
internal sealed class TravelLegTracker
{
    internal sealed class Place
    {
        internal string SystemId { get; }
        internal string? PoiId { get; }
        internal Place(string systemId, string? poiId)
        {
            if (string.IsNullOrWhiteSpace(systemId)) throw new ArgumentException("System identity required.", nameof(systemId));
            if (poiId != null && string.IsNullOrWhiteSpace(poiId)) throw new ArgumentException("Use null for empty space.", nameof(poiId));
            SystemId = systemId; PoiId = poiId;
        }
    }
    internal enum Kind { InitialPlacement, Requested, Departed, Arrived, Cancelled }
    internal sealed class Leg
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal Guid Session { get; }
        internal Place? Origin { get; }
        internal Place Requested { get; }
        internal Leg(Guid session, Place? origin, Place requested) { Session = session; Origin = origin; Requested = requested; }
    }
    internal sealed class Fact
    {
        internal Guid Session { get; }
        internal Guid? Operation { get; }
        internal Kind Transition { get; }
        internal Place? Location { get; }
        internal double? DwellSeconds { get; }
        internal Fact(Guid session, Guid? operation, Kind transition, Place? location, double? dwell = null)
        { Session = session; Operation = operation; Transition = transition; Location = location; DwellSeconds = dwell; }
    }
    private Guid? _session;
    private Leg? _pending;
    private double? _since;
    private bool _placed, _departed;
    private readonly List<Fact> _facts = new();
    internal Place? Current { get; private set; }

    internal void Reset(Guid? session)
    {
        if (session == Guid.Empty) throw new ArgumentException("Empty session identity.", nameof(session));
        _session = session; _pending = null; Current = null; _since = null; _placed = false; _departed = false; _facts.Clear();
    }
    internal void PlaceInitially(Guid session, Place place, double now, bool ready)
    {
        if (_session != session || _placed || _pending != null || !ready) return;
        CheckTime(now);
        Current = place ?? throw new ArgumentNullException(nameof(place)); _since = now; _placed = true;
        _facts.Add(new Fact(session, null, Kind.InitialPlacement, place));
    }
    internal Leg? Request(Guid session, Place requested)
    {
        if (_session != session) return null;
        if (requested == null) throw new ArgumentNullException(nameof(requested));
        if (_pending != null) Cancel(_pending);
        _pending = new Leg(session, Current, requested); _departed = false;
        _facts.Add(new Fact(session, _pending.Id, Kind.Requested, requested));
        return _pending;
    }
    internal void Depart(Leg leg, double now)
    {
        if (!Owns(leg) || _departed) return;
        CheckTime(now);
        double? dwell = _since.HasValue && now >= _since.Value ? now - _since.Value : null;
        _departed = true;
        _facts.Add(new Fact(leg.Session, leg.Id, Kind.Departed, Current, dwell));
        Current = null; _since = null;
    }
    internal void Arrive(Leg leg, Place actual, double now, bool ready)
    {
        if (!Owns(leg) || !_departed || !ready) return;
        CheckTime(now);
        Current = actual ?? throw new ArgumentNullException(nameof(actual)); _since = now; _placed = true;
        _pending = null;
        // Tutorial redirects and other verified alternatives preserve the requested
        // destination on the leg, but report the actual observed destination here.
        _facts.Add(new Fact(leg.Session, leg.Id, Kind.Arrived, actual));
    }
    internal void Cancel(Leg leg)
    {
        if (!Owns(leg)) return;
        _pending = null;
        _facts.Add(new Fact(leg.Session, leg.Id, Kind.Cancelled, Current));
    }
    internal Fact[] Drain()
    {
        var result = _facts.ToArray(); _facts.Clear(); return result;
    }
    private bool Owns(Leg leg) => leg != null && ReferenceEquals(_pending, leg) && _session == leg.Session;
    private static void CheckTime(double now)
    {
        if (double.IsNaN(now) || double.IsInfinity(now) || now < 0) throw new ArgumentOutOfRangeException(nameof(now));
    }
}
