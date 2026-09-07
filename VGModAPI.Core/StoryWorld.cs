using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>Outcome of one native operation. Nothing is ever assumed to have happened.</summary>
internal enum StoryWorldStatus
{
    /// <summary>The native world performed it and the API verified the result.</summary>
    Applied,
    /// <summary>The world already holds that identifier or mission; nothing was replaced.</summary>
    AlreadyPresent,
    /// <summary>The world declined, or the verification after the call did not show the expected result.</summary>
    Refused,
    /// <summary>No usable world right now (no player, no session, bindings unavailable).</summary>
    Unavailable
}

internal readonly struct StoryWorldResult
{
    internal StoryWorldStatus Status { get; }
    internal string Detail { get; }
    internal StoryWorldResult(StoryWorldStatus status, string detail = "")
    { Status = status; Detail = detail ?? ""; }
    internal bool Applied => Status == StoryWorldStatus.Applied;
    internal static StoryWorldResult Ok => new(StoryWorldStatus.Applied);
}

/// <summary>What the world currently holds for API-owned identifiers, read in one pass.</summary>
internal sealed class StoryWorldSnapshot
{
    internal IReadOnlyCollection<string> Installed { get; }
    internal IReadOnlyCollection<string> Active { get; }
    internal IReadOnlyCollection<string> Archived { get; }
    internal StoryWorldSnapshot(IReadOnlyCollection<string> installed, IReadOnlyCollection<string> active, IReadOnlyCollection<string> archived)
    {
        Installed = installed ?? throw new ArgumentNullException(nameof(installed));
        Active = active ?? throw new ArgumentNullException(nameof(active));
        Archived = archived ?? throw new ArgumentNullException(nameof(archived));
    }
}

/// <summary>
/// The narrow native surface the story module drives. It is an INTERFACE so the module's policy is
/// testable without the game, and so the reflection-bound implementation stays a thin adapter with no
/// policy of its own. Every member is Unity-main-thread-only and must not throw: a native failure is
/// reported as a refusal, because the module has to decide what to record, and it must never record
/// an acceptance the world did not actually perform.
/// </summary>
internal interface IStoryWorld
{
    /// <summary>Identifiers the world's story catalog already holds, ours and everyone else's.</summary>
    IReadOnlyCollection<string> InstalledIdentifiers();

    /// <summary>
    /// Whether the game knows this faction identity. A mission must carry a real source faction: the
    /// game writes it unconditionally when it saves, so an unknown identity is refused at
    /// registration instead of producing a mission that breaks the player's save.
    /// </summary>
    bool KnowsFaction(string factionId);

    /// <summary>
    /// Whether the loaded galaxy holds this point of interest, or null when no galaxy is loaded and
    /// nothing can be asserted. A travel objective aimed at a guid the world does not have is a
    /// mission that could never be completed.
    /// </summary>
    bool? KnowsPointOfInterest(string guid);

    /// <summary>
    /// Installs a definition under an identifier the API owns. Vanilla's own registration REPLACES a
    /// duplicate, so an identifier the API did not install is reported as already present and left
    /// exactly as it was.
    /// </summary>
    StoryWorldResult Install(string identifier, StoryMissionDefinition definition);

    /// <summary>Removes an installed definition only while the catalog entry is still the one this API installed.</summary>
    bool Uninstall(string identifier);

    /// <summary>
    /// Asks the world to accept a mission built from the installed definition, and verifies afterwards
    /// that it is actually held by the player. A duplicate story identifier, an unavailable player or
    /// an unverifiable result is a refusal, never a silent success.
    /// </summary>
    StoryWorldResult Accept(string identifier);

    /// <summary>
    /// Ends an API-owned mission the world still holds. Only identifiers this API installed are ever
    /// targeted, and only the outcomes that are the caller's to declare; a completion the world has
    /// not performed is never fabricated and no reward is granted by the API.
    /// </summary>
    StoryWorldResult Release(string identifier, StoryOutcome outcome);

    /// <summary>
    /// Undoes an acceptance this API just made, because the record could not be committed after it.
    /// It targets the EXACT mission object that acceptance produced, removes it without archiving,
    /// and verifies the world no longer holds it. A failed rollback is reported, never assumed.
    /// </summary>
    StoryWorldResult RollbackAccept(string identifier);

    /// <summary>The world's current view of API-owned identifiers, or null when there is no usable world.</summary>
    StoryWorldSnapshot? Snapshot();
}

/// <summary>
/// What the story module offers the native guards when the game's own UI performs a remove-and-re-add
/// of an owned mission. The guards cannot decide that alone: only the module knows which occurrence
/// the identifier belongs to and what its removal means.
/// </summary>
internal interface IStoryUiTransaction
{
    /// <summary>
    /// A UI abandon/retry of this identifier is about to run. Returning false refuses it, so the game
    /// does not remove the mission at all. Returning true suspends the outcome the removal would
    /// otherwise record and holds the catalog entry, so a retry can re-add the same occurrence.
    /// </summary>
    bool BeginAbandon(string identifier);

    /// <summary>
    /// The UI operation finished. <paramref name="stillHeld"/> says whether the game holds the
    /// mission again — a retry re-added it — which settles what the removal meant.
    /// </summary>
    void EndAbandon(string identifier, bool stillHeld);
}
