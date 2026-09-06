using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace VGModAPI.Qualification;

/// <summary>
/// The immutable native owner a cross-system case is allowed to drive: the exact
/// <c>TravelManager</c> instance and player captured at that case's own fresh fixture load and
/// readiness boundary, together with the session they belong to.
///
/// A qualification case loads its fixture itself, and that load DESTROYS the previous scene's
/// manager. A cached manager reference survives that as a live-looking managed reference whose
/// Unity object is gone ("fake null"), and driving it reaches native code that starts a coroutine
/// on a destroyed behaviour. So every drive and every observation must first prove that the
/// captured owner is still the live current one; a destroyed or replaced instance is a clear
/// recorded failure, never something to re-bind to and never something to drive.
///
/// The liveness test is injected (<c>alive</c>) because only the runtime knows Unity's
/// destroyed-object semantics: the pilot passes the Unity-aware check, host tests pass a
/// fake-null simulator. This type itself has no Unity, BepInEx or reflection dependency, so the
/// ownership rules are host regressions rather than prose.
/// </summary>
internal sealed class NativeCaseOwner
{
    internal Guid Session { get; }
    internal object TravelManager { get; }
    internal object Player { get; }
    internal string SystemId { get; }
    internal string? StartPoiId { get; }

    internal NativeCaseOwner(Guid session, object travelManager, object player, string systemId, string? startPoiId)
    {
        if (session == Guid.Empty) throw new ArgumentException("A case owner requires the session it was captured in.", nameof(session));
        if (string.IsNullOrEmpty(systemId)) throw new ArgumentException("A case owner requires the captured native system.", nameof(systemId));
        Session = session;
        TravelManager = travelManager ?? throw new ArgumentNullException(nameof(travelManager));
        Player = player ?? throw new ArgumentNullException(nameof(player));
        SystemId = systemId;
        StartPoiId = startPoiId;
    }

    /// <summary>
    /// Null when the captured owner is still live and current, otherwise the exact reason the
    /// requested action must NOT be driven, with identity/liveness/session diagnostics.
    /// Rebinding is deliberately impossible here: an arbitrary replacement can never be adopted
    /// by a case that was prepared against a different session or a different manager instance.
    /// </summary>
    internal string? CheckCurrent(string action, Guid? currentSession, object? currentManager, object? currentPlayer, Func<object?, bool> alive)
    {
        if (alive == null) throw new ArgumentNullException(nameof(alive));
        string Refuse(string reason) => "Refusing " + action + ": " + reason + ". "
            + Describe(currentSession, currentManager, currentPlayer, alive);
        if (currentSession != Session) return Refuse("the live session is not the session this case was prepared in");
        if (!alive(TravelManager)) return Refuse("the captured native travel manager was destroyed by a later scene/fixture load");
        if (!alive(currentManager)) return Refuse("there is no live native travel manager");
        if (!ReferenceEquals(TravelManager, currentManager)) return Refuse("the live native travel manager is a different instance");
        if (!alive(Player)) return Refuse("the captured native player is gone");
        if (!ReferenceEquals(Player, currentPlayer)) return Refuse("the live native player is a different instance");
        return null;
    }

    /// <summary>Identity, liveness and session diagnostics for a drive or observation failure.</summary>
    internal string Describe(Guid? currentSession, object? currentManager, object? currentPlayer, Func<object?, bool> alive)
        => "caseSession=" + Session.ToString()
            + "; liveSession=" + (currentSession?.ToString() ?? "<none>")
            + "; capturedManager=" + Identity(TravelManager, alive)
            + "; liveManager=" + Identity(currentManager, alive)
            + "; capturedPlayer=" + Identity(Player, alive)
            + "; livePlayer=" + Identity(currentPlayer, alive)
            + "; capturedLocation=" + SystemId + ":" + (StartPoiId ?? "<empty space>");

    /// <summary>
    /// Reference identity plus liveness. The reference is deliberately reported even when the
    /// object is destroyed: a non-null reference to a destroyed native object is exactly the trap
    /// this owner exists to catch.
    /// </summary>
    internal static string Identity(object? value, Func<object?, bool> alive)
        => value == null
            ? "<null>"
            : value.GetType().Name + "#" + RuntimeHelpers.GetHashCode(value).ToString(CultureInfo.InvariantCulture)
                + (alive(value) ? ",alive" : ",destroyed");
}
