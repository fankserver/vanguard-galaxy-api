using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VGModAPI;

/// <summary>
/// Immutable persistent moored-ship declaration: one friendly ship of an exact class, placed at an
/// authored offset beside a station POI, held on station (no docking or auto-AI, never boardable),
/// optionally kept alive through unit protection. Register before starting a session.
/// </summary>
public sealed class AuthoredShipDefinition
{
    public string LocalId { get; }
    public int Revision { get; }
    /// <summary>Display name and commander callsign for this instance; the ship class identity is never renamed.</summary>
    public string Name { get; }
    public string ShipClassId { get; }
    public string FactionId { get; }
    public float OffsetX { get; }
    public float OffsetY { get; }
    /// <summary>Keeps the moored instance alive through unit protection while its occurrence is owned.</summary>
    public bool Protect { get; }
    public AuthoredShipDefinition(string localId, int revision, string name, string shipClassId, string factionId,
        float offsetX, float offsetY, bool protect = true)
    {
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (string.IsNullOrWhiteSpace(shipClassId)) throw new ArgumentException("An exact ship class is required.", nameof(shipClassId));
        if (string.IsNullOrWhiteSpace(factionId)) throw new ArgumentException("An existing faction identity is required.", nameof(factionId));
        if (float.IsNaN(offsetX) || float.IsInfinity(offsetX) || float.IsNaN(offsetY) || float.IsInfinity(offsetY)
            || Math.Abs(offsetX) > 10000 || Math.Abs(offsetY) > 10000) throw new ArgumentOutOfRangeException(nameof(offsetX));
        Revision = revision; ShipClassId = shipClassId; FactionId = factionId;
        OffsetX = offsetX; OffsetY = offsetY; Protect = protect;
    }
}

/// <summary>Typed per-occurrence moored-ship state.</summary>
public sealed class AuthoredShipState
{
    public AuthoredSystemReconstructionStatus Status { get; }
    public AuthoredSystemFailureReason? Reason { get; }
    /// <summary>Persistent unit-data identity; populated only while reconstructed.</summary>
    public string? UnitId { get; }
    public bool Reconstructed => Status == AuthoredSystemReconstructionStatus.Reconstructed;
    public AuthoredShipState(AuthoredSystemReconstructionStatus status, AuthoredSystemFailureReason? reason = null, string? unitId = null)
    { Status = status; Reason = reason; UnitId = unitId; }
}

/// <summary>One owned moored-ship occurrence for a single captured game, following the uniform occurrence contract.</summary>
public interface IAuthoredShip
{
    string OccurrenceKey { get; }
    AuthoredShipDefinition Definition { get; }
    AuthoredShipState State { get; }
    string? UnitId { get; }
    AuthoredActionResult LastAction { get; }
    event Action<IAuthoredShip>? Changed;
}

public sealed class AuthoredShipFailure
{
    public IAuthoredShip Occurrence { get; }
    public AuthoredSystemFailureReason Reason { get; }
    public AuthoredShipFailure(IAuthoredShip occurrence, AuthoredSystemFailureReason reason)
    { Occurrence = occurrence ?? throw new ArgumentNullException(nameof(occurrence)); Reason = reason; }
}

public sealed class AuthoredShipsSettledEvent
{
    public Guid SessionId { get; }
    public IReadOnlyList<IAuthoredShip> Reconstructed { get; }
    public IReadOnlyList<AuthoredShipFailure> Failures { get; }
    public bool HasFailures => Failures.Count > 0;
    public AuthoredShipsSettledEvent(Guid sessionId, IEnumerable<IAuthoredShip> reconstructed, IEnumerable<AuthoredShipFailure> failures)
    {
        SessionId = sessionId;
        Reconstructed = new ReadOnlyCollection<IAuthoredShip>(new List<IAuthoredShip>(reconstructed ?? throw new ArgumentNullException(nameof(reconstructed))));
        Failures = new ReadOnlyCollection<AuthoredShipFailure>(new List<AuthoredShipFailure>(failures ?? throw new ArgumentNullException(nameof(failures))));
    }
}
