using System;
using System.Collections.Generic;
using VGModAPI.Core;

namespace VGModAPI.Tests;

/// <summary>Stateful test double for the authored-pocket native seam.</summary>
internal sealed class FakePocketSystemNative : IPocketSystemNative
{
    internal bool FailCreate;
    internal bool ThrowOnCreate;
    internal bool ThrowOnApply;
    internal int Ambiguity = 1;
    internal int NextId;
    internal readonly Dictionary<string, (string Entrance, string Pocket)> Systems = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, bool> Open = new(StringComparer.Ordinal);
    internal int ApplyCalls;
    internal bool PlayerInside;
    internal bool FailDissolve;
    internal bool ThrowOnDissolve;
    internal int DissolveCalls;
    internal readonly List<PocketSystemPlacement> CreatedPlacements = new();
    internal readonly List<string?> CreatedFactionIds = new();

    public PocketSystemInfo? CreatePocket(Guid session, string anchorSystemId, PocketSystemPlacement placement, string? factionId)
    {
        if (ThrowOnCreate) throw new InvalidOperationException("native create fault");
        if (FailCreate) return null;
        CreatedPlacements.Add(placement);
        CreatedFactionIds.Add(factionId);
        NextId++;
        string sid = "sys-" + NextId;
        Systems[sid] = ("en-" + NextId, "pk-" + NextId);
        Open[sid] = false;
        return new PocketSystemInfo(sid, "en-" + NextId, "pk-" + NextId);
    }
    public PocketSystemInfo? ResolvePocket(Guid session, string systemId)
    {
        if (systemId == null || !Systems.TryGetValue(systemId, out var pair)) return null;
        return new PocketSystemInfo(systemId, pair.Entrance, pair.Pocket);
    }
    public int AmbiguousCount(Guid session, string systemId)
        => systemId != null && Systems.ContainsKey(systemId) ? Ambiguity : 0;
    public bool ApplyOpen(Guid session, string entranceGateId, string pocketGateId, bool open)
    {
        ApplyCalls++;
        if (ThrowOnApply) throw new InvalidOperationException("native apply fault");
        string? sid = EntranceToSystem(entranceGateId);
        if (sid != null) Open[sid] = open;
        return true;
    }
    public bool IsOpen(Guid session, string entranceGateId, string pocketGateId)
    {
        string? sid = EntranceToSystem(entranceGateId);
        return sid != null && Open.TryGetValue(sid, out var openValue) && openValue;
    }
    public PocketDissolveOutcome DissolvePocket(Guid session, string systemId, string entranceGateId, string pocketGateId)
    {
        DissolveCalls++;
        if (ThrowOnDissolve) throw new InvalidOperationException("native dissolve fault");
        if (systemId == null || !Systems.TryGetValue(systemId, out var pair) || pair.Entrance != entranceGateId || pair.Pocket != pocketGateId)
            return PocketDissolveOutcome.Missing;
        if (PlayerInside) return PocketDissolveOutcome.PlayerInside;
        if (FailDissolve) return PocketDissolveOutcome.Failed;
        Systems.Remove(systemId);
        Open.Remove(systemId);
        return PocketDissolveOutcome.Dissolved;
    }
    public void BeginPass(Guid session) { }
    public void EndPass() { }
    private string? EntranceToSystem(string entrance)
    {
        foreach (var pair in Systems) if (pair.Value.Entrance == entrance) return pair.Key;
        return null;
    }
}
