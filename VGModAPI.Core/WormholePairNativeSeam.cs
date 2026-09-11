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

internal interface IWormholePairNative
{
    WormholePairInfo? CreatePair(Guid session, string name, string firstSystemId, string secondSystemId, bool open);
    WormholePairInfo? ResolvePair(Guid session, string firstSystemId, string secondSystemId, string firstPoiId, string secondPoiId);
    int AmbiguousCount(Guid session, string poiId);
    bool ApplyOpen(Guid session, string firstPoiId, string secondPoiId, bool open);
    void BeginPass(Guid session);
    void EndPass();
}
