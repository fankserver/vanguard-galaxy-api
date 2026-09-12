using System;

namespace VGModAPI.Core;

internal sealed class WormholePairInfo
{
    internal string FirstPoiId { get; }
    internal string SecondPoiId { get; }
    internal bool Open { get; }
    internal WormholePairInfo(string firstPoiId, string secondPoiId, bool open)
    { FirstPoiId = firstPoiId; SecondPoiId = secondPoiId; Open = open; }
}

/// <summary>Typed outcome of a native wormhole-pair removal attempt.</summary>
internal enum WormholeRemoveOutcome
{
    /// <summary>Both wormhole POIs were removed from their systems; the pair is gone from the live map.</summary>
    Removed,
    /// <summary>The player's current location/Poi or a waypoint is at one of the wormholes; nothing was removed.</summary>
    PlayerInside,
    /// <summary>One or both owned wormhole POIs are not currently present natively; nothing was removed.</summary>
    Missing,
    /// <summary>The native removal could not be performed or verified; the map may be unchanged.</summary>
    Failed
}

internal interface IWormholePairNative
{
    WormholePairInfo? CreatePair(Guid session, string name, string firstSystemId, string secondSystemId, bool open);
    WormholePairInfo? ResolvePair(Guid session, string firstSystemId, string secondSystemId, string firstPoiId, string secondPoiId);
    int AmbiguousCount(Guid session, string poiId);
    bool ApplyOpen(Guid session, string firstPoiId, string secondPoiId, bool open);
    /// <summary>Removes both owned wormhole POIs from their systems. Refuses while the player is at/inside either.</summary>
    WormholeRemoveOutcome RemoveWormhole(Guid session, string firstPoiId, string secondPoiId);
    void BeginPass(Guid session);
    void EndPass();
}
