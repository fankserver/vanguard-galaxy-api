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

    /// <summary>
    /// Whether the guards can currently decide about owned content. False after a scan the guard
    /// could not complete, which is exactly when no new owned content may be installed or accepted.
    /// </summary>
    internal bool Healthy => _degradedReason == null;

    /// <summary>
    /// Re-establishes health by actually completing an ownership scan of what the player holds. A
    /// degraded guard is never restored by clearing a message: it is restored by reading the world
    /// successfully in the session that replaced the one it failed in.
    /// </summary>
    internal bool VerifyHealthy()
    {
        try
        {
            int missions = 0, objectives = 0;
            foreach (var mission in Held())
            {
                if (++missions > MaxScannedMissions) return false;
                _ = Blocks(mission);
                foreach (var _ in _guard.Objectives(mission))
                    if (++objectives > MaxScannedObjectives) return false;
            }
            _degradedReason = null;
            return true;
        }
        catch (Exception error) { Report(error); return false; }
    }

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
            // A guard that cannot decide vouches for nothing: the admissions it was answering with are
            // withdrawn at once, so owned content is quarantined rather than trusted on stale answers.
            try { _protection.WithdrawAll("the guard could not decide: " + reason); } catch { /* never fault a guard */ }
            try { _degraded?.Invoke(reason); } catch { /* reporting must never fault a guard */ }
        }
        return true;
    }

    /// <summary>
    /// The game's own abandon/retry button on an owned mission. A quarantined mission is refused
    /// outright, so the raw object stays exactly where the save put it. An admitted one is handed to
    /// the module, which decides whether the removal it is about to see is a retry or an ending.
    /// </summary>
    internal bool AllowAbandon(object? mission, out StoryAbandonState? state)
    {
        state = null;
        if (mission == null) return true;
        try
        {
            var storyId = _guard.StoryId(mission);
            if (storyId == null || !_protection.IsOwnedNamespace(storyId)) return true;
            if (_protection.IsQuarantined(storyId)) return false;
            // The game re-adds `nextMissionOnFailed ?? storyId`. A follow-up identifier would install
            // something this module never admitted, so an owned mission carrying one is refused.
            if (_guard.NextMissionOnFailed(mission) != null) return false;
            // The game removes BY REFERENCE and re-adds with the duplicate check forced off, so a
            // stale object carrying an admitted identifier would remove nothing and add a second live
            // mission for it. The button is therefore only allowed for an object the player actually
            // holds, and only when that identifier is held exactly once.
            int matches = 0;
            bool present = false;
            foreach (var held in Held())
            {
                if (ReferenceEquals(held, mission)) present = true;
                if (string.Equals(_guard.StoryId(held), storyId, StringComparison.Ordinal)) matches++;
            }
            if (!present || matches != 1) return false;
            var token = Transactions?.BeginAbandon(storyId);
            if (token == null) return false;
            state = new StoryAbandonState(storyId, mission, token);
            return true;
        }
        catch (Exception error) { Report(error); state = null; return !_guard.IsMission(mission); }
    }

    /// <summary>
    /// Settles a UI abandon or retry by looking at what the game actually holds afterwards. An
    /// inspection failure is reported as UNKNOWN, never as an ending: inventing "it is gone" would
    /// retire an occurrence and drop its catalog entry over a world nobody could read.
    /// </summary>
    internal void EndAbandon(StoryAbandonState state)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        // Checked BEFORE anything is read, settled or degraded. A finalizer can arrive after a
        // synchronous callback replaced the session, or after another transaction opened: acting then
        // would inspect a world this transaction never opened, and a failed read of THAT world would
        // withdraw the new session's admissions over a transaction that no longer exists.
        if (Transactions?.IsTransactionCurrent(state.Token) != true) return;
        StoryAbandonSettlement settlement;
        try
        {
            bool original = false;
            int replacements = 0;
            foreach (var held in Held())
            {
                if (ReferenceEquals(held, state.Mission)) { original = true; continue; }
                if (string.Equals(_guard.StoryId(held), state.Identifier, StringComparison.Ordinal)) replacements++;
            }
            settlement = original
                ? (replacements == 0 ? StoryAbandonSettlement.OriginalStillHeld : StoryAbandonSettlement.UnknownOrAmbiguous)
                : replacements switch
                {
                    0 => StoryAbandonSettlement.NoneHeld,
                    1 => StoryAbandonSettlement.OneReplacementHeld,
                    _ => StoryAbandonSettlement.UnknownOrAmbiguous
                };
        }
        catch (Exception error)
        {
            Report(error);
            Degrade("the guard could not read what the world held after an abandon (" + error.GetType().Name + ")");
            settlement = StoryAbandonSettlement.UnknownOrAmbiguous;
        }
        try { Transactions?.EndAbandon(state.Token, settlement); }
        catch (Exception error) { Report(error); }
    }

    private IEnumerable<object> Held() => _heldMissions() ?? Array.Empty<object>();

    private void Report(Exception error) => _fault?.Invoke(error);
}

/// <summary>
/// What one accepted abandon/retry is about: the identifier, and the exact object the button was
/// pressed on. The object matters because a retry produces a NEW one.
/// </summary>
internal sealed class StoryAbandonState
{
    internal string Identifier { get; }
    internal object Mission { get; }
    /// <summary>The transaction this state belongs to, which is what makes a late finalizer harmless.</summary>
    internal StoryUiTransactionToken Token { get; }
    internal StoryAbandonState(string identifier, object mission, StoryUiTransactionToken token)
    { Identifier = identifier; Mission = mission; Token = token; }
}
