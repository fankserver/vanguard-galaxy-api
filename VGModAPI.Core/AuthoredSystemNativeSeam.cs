using System;

namespace VGModAPI.Core;

/// <summary>Opaque native pocket identity. Core is Unity-free; only bounded string identities cross the seam.</summary>
internal sealed class AuthoredSystemPocketInfo
{
    internal string SystemId { get; }
    internal string EntranceGateId { get; }
    internal string PocketGateId { get; }
    internal AuthoredSystemPocketInfo(string systemId, string entranceGateId, string pocketGateId)
    { SystemId = systemId; EntranceGateId = entranceGateId; PocketGateId = pocketGateId; }
}

/// <summary>Typed outcome of a native pocket dissolution attempt.</summary>
internal enum PocketDissolveOutcome
{
    /// <summary>The pocket system, both paired gates and its contained POIs were removed from the live map.</summary>
    Dissolved,
    /// <summary>The player's current system, current location or a waypoint is inside the pocket; nothing was removed.</summary>
    PlayerInside,
    /// <summary>The owned pocket or its paired gates are not currently present natively; nothing was removed.</summary>
    Missing,
    /// <summary>The native removal could not be performed or verified; the map may be unchanged.</summary>
    Failed
}

/// <summary>
/// Reflection-driven native seam for authored pocket systems. The installed implementation resolves
/// against the live game; tests supply fakes. Core never references native Unity types.
/// </summary>
internal interface IAuthoredSystemNative
{
    /// <summary>Creates an enclosed pocket next to the given anchor system. Returns null when no free position exists (a typed failure).</summary>
    AuthoredSystemPocketInfo? CreatePocket(Guid session, string anchorSystemId);
    /// <summary>Re-resolves the owned pocket structurally by its systems identity; null until native construction surfaces it.</summary>
    AuthoredSystemPocketInfo? ResolvePocket(Guid session, string systemId);
    /// <summary>Number of native systems currently bearing the given identity (ambiguity detection).</summary>
    int AmbiguousCount(Guid session, string systemId);
    /// <summary>Applies the declared gate state to both paired gates (unhide + open/close together). Returns success.</summary>
    bool ApplyOpen(Guid session, string entranceGateId, string pocketGateId, bool open);
    /// <summary>Reads the current paired-gate open state (both open and unhidden means open).</summary>
    bool IsOpen(Guid session, string entranceGateId, string pocketGateId);
    /// <summary>Removes the owned pocket, its paired gates and its contained POIs. Refuses while the player is inside.</summary>
    PocketDissolveOutcome DissolvePocket(Guid session, string systemId, string entranceGateId, string pocketGateId);
    /// <summary>Begins a single reconciliation pass over the session's map; read paths reuse one snapshot until EndPass.</summary>
    void BeginPass(Guid session);
    /// <summary>Ends a reconciliation pass, releasing the cached snapshot.</summary>
    void EndPass();
}
