using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal enum DungeonTerminalProgress { NotStarted, Attempted, Completed }

/// <summary>Persistent operation identity is independent of an authored location occurrence and runtime handles.</summary>
internal sealed class DungeonOperationResumeState
{
    internal Guid Id { get; }
    internal Guid LocationId { get; }
    internal Guid? ContentOccurrence { get; }
    internal string AttackerShipId { get; }
    internal string DungeonType { get; }
    internal string NativePhase { get; }
    internal string Outcome { get; }
    internal string MissionProtection { get; }
    internal DungeonTerminalProgress TerminalProgress { get; }
    internal bool Autonomous { get; }
    internal bool WalkDispatched { get; }
    internal DungeonOperationOptions? Options { get; }
    internal IReadOnlyList<DungeonDonorApproachState> Donors { get; }
    internal DungeonOperationResumeState(Guid id, Guid locationId, Guid? contentOccurrence, string attackerShipId, string dungeonType,
        string nativePhase, string outcome, string missionProtection, DungeonTerminalProgress terminalProgress, bool autonomous, DungeonOperationOptions? options = null, IEnumerable<DungeonDonorApproachState>? donors = null, bool walkDispatched = false)
    {
        if (id == Guid.Empty || locationId == Guid.Empty || contentOccurrence == Guid.Empty || !Enum.IsDefined(typeof(DungeonTerminalProgress), terminalProgress))
            throw new ArgumentException("Invalid persistent operation identity or terminal state.");
        foreach (var text in new[] { attackerShipId, dungeonType, nativePhase, outcome, missionProtection })
            if (text == null || text.Length > 128 || text.IndexOf('\0') >= 0) throw new ArgumentException("Invalid saved operation field.");
        if (string.IsNullOrWhiteSpace(attackerShipId) || string.IsNullOrWhiteSpace(dungeonType) || string.IsNullOrWhiteSpace(nativePhase))
            throw new ArgumentException("Operation recipient, type and phase are required.");
        Id = id; LocationId = locationId; ContentOccurrence = contentOccurrence; AttackerShipId = attackerShipId;
        DungeonType = dungeonType; NativePhase = nativePhase; Outcome = outcome; MissionProtection = missionProtection;
        var reservations = (donors ?? Array.Empty<DungeonDonorApproachState>()).Take(65).ToArray();
        if (reservations.Length > 64 || reservations.Any(item => item == null) || reservations.Select(item => item.ShipId).Distinct(StringComparer.Ordinal).Count() != reservations.Length)
            throw new ArgumentException("Invalid donor reservations.");
        Donors = Array.AsReadOnly(reservations);
        TerminalProgress = terminalProgress; Autonomous = autonomous; Options = options; WalkDispatched = walkDispatched;
    }
    internal bool MayStartTerminalEffects => TerminalProgress == DungeonTerminalProgress.NotStarted;
}
