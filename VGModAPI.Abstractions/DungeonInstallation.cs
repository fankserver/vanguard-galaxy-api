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
}
