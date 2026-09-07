using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>
/// What the native guards call. It answers one question per entry point — may this mission, or this
/// objective, advance? — and answers it fail-closed for anything under this API's reserved namespace
/// that nobody currently vouches for.
///
/// Objective ownership is resolved AGAINST THE CURRENT PLAYER on every call rather than cached: a
/// cache keyed on admissions goes stale exactly where it matters, when a second save is loaded into
/// the same process with the story module switched off and nothing bumps it. The scan is bounded, and
/// when it cannot be completed the objective is refused and the protection reports itself degraded,
/// rather than quietly letting content through.
/// </summary>
internal sealed class StoryQuarantine
{
    /// <summary>Bounds the per-call ownership scan; a player holding more than this is not scanned but refused.</summary>
    internal const int MaxScannedMissions = 256;
    internal const int MaxScannedObjectives = 4096;

    private readonly StoryProtectionGuard _guard;
    private readonly StoryProtection _protection;
    private readonly Func<IEnumerable<object>> _heldMissions;
    private readonly Action<Exception>? _fault;
    private readonly Action<string>? _degraded;
    private string? _degradedReason;

    internal StoryQuarantine(StoryProtectionGuard guard, StoryProtection protection,
        Func<IEnumerable<object>> heldMissions, Action<Exception>? fault = null, Action<string>? degraded = null)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        _heldMissions = heldMissions ?? throw new ArgumentNullException(nameof(heldMissions));
        _fault = fault;
        _degraded = degraded;
    }

    /// <summary>The story module's own handler for a UI abandon or retry; null when no module is present.</summary>
    internal IStoryUiTransaction? Transactions { get; set; }

    /// <summary>Non-null once a scan could not be completed; the protection is degraded until the next session.</summary>
    internal string? DegradedReason => _degradedReason;

    internal void ClearDegraded() => _degradedReason = null;

    /// <summary>
    /// True when this mission must not progress. A guard that cannot decide treats the mission as
    /// quarantined ONLY if it is one of ours; anything else is left alone, because refusing another
    /// mod's or the game's own content would be its own kind of damage.
    /// </summary>
    internal bool Blocks(object? mission)
    {
        if (mission == null) return false;
        try { return _protection.IsQuarantined(_guard.StoryId(mission)); }
        catch (Exception error) { Report(error); return _guard.IsMission(mission); }
    }

    /// <summary>
    /// Whether this objective belongs to a mission that must not progress. Ownership is resolved from
    /// the player's CURRENT missions each time, so a save loaded later in the same process is seen.
    /// </summary>
    internal bool BlocksObjective(object? objective)
    {
        if (objective == null) return false;
        try
        {
            int missions = 0, objectives = 0;
            foreach (var mission in _heldMissions() ?? Array.Empty<object>())
            {
                if (++missions > MaxScannedMissions) return Degrade("the player holds more missions than the guard can scan");
                bool quarantined = Blocks(mission);
                foreach (var candidate in _guard.Objectives(mission))
                {
                    if (++objectives > MaxScannedObjectives) return Degrade("the player holds more objectives than the guard can scan");
                    if (ReferenceEquals(candidate, objective)) return quarantined;
                }
            }
            // An objective that belongs to no mission the player holds is not ours to judge.
            return false;
        }
        catch (Exception error)
        {
            Report(error);
            // Ownership could not be established, so it cannot be ruled out: refuse this objective and
            // say so, rather than let a possibly owned one advance because a lookup threw.
            return Degrade("the guard could not read the player's missions (" + error.GetType().Name + ")");
        }
    }

    private bool Degrade(string reason)
    {
        if (_degradedReason == null)
        {
            _degradedReason = reason;
            try { _degraded?.Invoke(reason); } catch { /* reporting must never fault a guard */ }
        }
        return true;
    }

    /// <summary>
    /// The game's own abandon/retry button on an owned mission. A quarantined mission is refused
    /// outright, so the raw object stays exactly where the save put it. An admitted one is handed to
    /// the module, which decides whether the removal it is about to see is a retry or an ending.
    /// </summary>
    internal bool AllowAbandon(object? mission, out string? identifier)
    {
        identifier = null;
        if (mission == null) return true;
        try
        {
            var storyId = _guard.StoryId(mission);
            if (storyId == null || !_protection.IsOwnedNamespace(storyId)) return true;
            if (_protection.IsQuarantined(storyId)) return false;
            // The game re-adds `nextMissionOnFailed ?? storyId`. A follow-up identifier would install
            // something this module never admitted, so an owned mission carrying one is refused.
            if (_guard.NextMissionOnFailed(mission) != null) return false;
            identifier = storyId;
            return Transactions?.BeginAbandon(storyId) ?? false;
        }
        catch (Exception error) { Report(error); identifier = null; return !_guard.IsMission(mission); }
    }

    /// <summary>Settles a UI abandon or retry by looking at what the game actually holds afterwards.</summary>
    internal void EndAbandon(string identifier)
    {
        try
        {
            bool stillHeld = _heldMissions().Any(mission => string.Equals(_guard.StoryId(mission), identifier, StringComparison.Ordinal));
            Transactions?.EndAbandon(identifier, stillHeld);
        }
        catch (Exception error)
        {
            Report(error);
            // The outcome could not be observed, so nothing is claimed about it: the module is told
            // the mission is gone, which is the conservative reading of a removal it already saw.
            try { Transactions?.EndAbandon(identifier, false); } catch (Exception inner) { Report(inner); }
        }
    }

    private void Report(Exception error) => _fault?.Invoke(error);
}
