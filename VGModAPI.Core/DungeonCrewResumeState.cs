using System;

namespace VGModAPI.Core;

/// <summary>Execution state omitted by the inspected native crew serializer.</summary>
internal sealed class DungeonCrewResumeState
{
    internal int DirectiveTarget { get; }
    internal float FleeDelay { get; }
    internal bool Withdrawing { get; }
    internal int RetreatOrigin { get; }
    internal float RecoveryProgress { get; }
    internal float DazedTime { get; }
    internal DungeonCrewResumeState(int directiveTarget, float fleeDelay, bool withdrawing, int retreatOrigin, float recoveryProgress, float dazedTime)
    {
        if (directiveTarget < -1 || retreatOrigin < -1 || !Finite(fleeDelay) || !Finite(recoveryProgress) || !Finite(dazedTime))
            throw new ArgumentException("Invalid saved crew execution state.");
        DirectiveTarget = directiveTarget; FleeDelay = fleeDelay; Withdrawing = withdrawing;
        RetreatOrigin = retreatOrigin; RecoveryProgress = recoveryProgress; DazedTime = dazedTime;
    }
    internal bool Fits(int compartmentCount) => compartmentCount > 0 && DirectiveTarget < compartmentCount && RetreatOrigin < compartmentCount;
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
