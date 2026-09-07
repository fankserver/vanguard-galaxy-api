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

    /// <summary>The world's current view of API-owned identifiers, or null when there is no usable world.</summary>
    StoryWorldSnapshot? Snapshot();
}
