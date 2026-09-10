using System;

namespace VGModAPI;

/// <summary>A synchronous owned-contact interaction. Native UI objects and callbacks are never persisted.</summary>
internal sealed class BarInteraction
{
    public Guid SessionId { get; }
    public BarPatronId PatronId { get; }
    public string StationId { get; }
    internal Func<bool> IsCurrent { get; }
    public BarInteraction(Guid sessionId, BarPatronId patronId, string stationId, Func<bool>? isCurrent = null)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(stationId)) throw new ArgumentException("A station is required.", nameof(stationId));
        SessionId = sessionId; PatronId = patronId; StationId = stationId; IsCurrent = isCurrent ?? (() => true);
    }
}
