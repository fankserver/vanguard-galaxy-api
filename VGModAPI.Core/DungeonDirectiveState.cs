using System;

namespace VGModAPI.Core;

/// <summary>A claiming slot refers only to the crew arrays in the same serialized simulation snapshot.</summary>
internal sealed class DungeonDirectiveState
{
    internal int Target { get; }
    internal int Priority { get; }
    internal string? RequiredCrew { get; }
    internal int Filter { get; }
    internal int ClaimingSlot { get; }
    internal bool Claimed => ClaimingSlot >= 0;
    internal DungeonDirectiveState(int target, int priority, string? requiredCrew, int filter, int claimingSlot)
    {
        if (target < 0 || priority < 0 || priority > 3 || filter < 0 || filter > 2 || claimingSlot < -1 || claimingSlot >= 4096 || requiredCrew?.Length > 128)
            throw new ArgumentException("Invalid saved crew directive.");
        Target = target; Priority = priority; RequiredCrew = requiredCrew; Filter = filter; ClaimingSlot = claimingSlot;
    }
}
