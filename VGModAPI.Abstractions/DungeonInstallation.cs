using System;

namespace VGModAPI;

/// <summary>A provider-lifetime view of an installation by persistent POI identity, not an ownership claim.
/// The POI may be absent or created later. Subscriptions survive save/load; runtime binding is API-owned.</summary>
public interface IDungeonInstallation
{
    string PoiId { get; }
    /// <summary>Accepted extraction request, not completed crew return. Delivered at a later safe gameplay
    /// boundary where normal follow-up actions are permitted. No replay; session-ended work is discarded.
    /// Remove handlers or dispose the owning dungeon provider to stop delivery.</summary>
    event Action? ExtractionStarted;
    /// <summary>
    /// Keeps this installation enterable while the declaration is held, so an authored boarding
    /// objective cannot be invalidated by ambient world damage before the player arrives. The
    /// station's parts — including docking — cannot be destroyed and its interior structure cannot
    /// collapse, and a target that was already unusable (destroyed docking, floored integrity or a
    /// collapsed interior) is restored to enterable. A live operation, the player's presence at the
    /// station and a legitimately cleared interior are never overridden. Holds across save/load and
    /// wherever the player is; declare once and retain, then dispose when the objective completes.
    /// Unrelated installations, dungeons and ship boardings stay completely vanilla.
    /// </summary>
    IDisposable KeepEnterable();
}
