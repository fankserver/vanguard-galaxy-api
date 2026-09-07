using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>
/// The authority that decides whether an API-owned mission the GAME is holding may progress.
///
/// A save keeps accepted missions as full objects, so an owned mission comes back whether or not the
/// module that owns it is present, enabled, bound or willing. Refusing API calls does nothing for it:
/// the game would keep updating it, completing it and paying its rewards with no provider behind it.
/// This type is what the native guards ask before any of that happens.
///
/// It is deliberately SEPARATE from the story module and fails CLOSED: with nothing admitted — module
/// absent, disabled, unbound, suspended, or a session that has not restored its state — every
/// identifier this API could have written is quarantined. Quarantine never touches the mission object
/// or the save: the mission stays in the player's list exactly as it was loaded, serializes exactly
/// as it was, and is simply not allowed to advance or pay out.
/// </summary>
internal sealed class StoryProtection
{
    private readonly Action? _checkThread;
    private readonly HashSet<string> _admitted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _reasons = new(StringComparer.Ordinal);
    private Guid _session;
    private string _state = "no session has admitted owned story content";
    /// <summary>Bumped whenever admissions change, so caches derived from them can notice.</summary>
    internal long Version { get; private set; } = 1;

    internal StoryProtection(Action? checkThread = null) => _checkThread = checkThread;

    /// <summary>Identifiers the owning module vouches for RIGHT NOW, in the session it vouched for them.</summary>
    internal void Admit(Guid session, IEnumerable<string> identifiers, string state)
    {
        _checkThread?.Invoke();
        _admitted.Clear();
        _session = session;
        _state = state ?? "";
        Version++;
        if (session == Guid.Empty || identifiers == null) return;
        foreach (var identifier in identifiers) if (!string.IsNullOrEmpty(identifier)) _admitted.Add(identifier);
    }

    /// <summary>Withdraws every admission. Used when the module suspends, faults, is disposed, or a session ends.</summary>
    internal void WithdrawAll(string state)
    {
        _checkThread?.Invoke();
        _admitted.Clear();
        _session = Guid.Empty;
        _state = state ?? "";
        Version++;
    }

    internal int AdmittedCount => _admitted.Count;
    internal string State => _state;

    /// <summary>Identifiers the guards actually refused, for a compatibility report; bounded and diagnostic only.</summary>
    internal IReadOnlyList<string> Quarantined => _reasons.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
    internal string? ReasonFor(string identifier) => _reasons.TryGetValue(identifier, out var reason) ? reason : null;

    /// <summary>
    /// Whether this story identifier may progress. Only identifiers this API could have written are
    /// ever considered: anything else — vanilla story content, another mod's content, no identifier at
    /// all — is not ours to judge and passes through untouched.
    /// </summary>
    internal bool IsQuarantined(string? storyId)
    {
        _checkThread?.Invoke();
        if (storyId == null || !StoryContentPolicy.TryParseOccurrenceIdentifier(storyId, out _, out _)) return false;
        if (_admitted.Contains(storyId)) return false;
        if (_reasons.Count < 256 && !_reasons.ContainsKey(storyId)) _reasons[storyId] = _state;
        return true;
    }
}
