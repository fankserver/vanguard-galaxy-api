using System;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class DungeonCrewResumeAdapter
{
    private readonly IBoardingTacticalNativeBindings _native;
    internal DungeonCrewResumeAdapter(IBoardingTacticalNativeBindings native) { _native = native; }
    internal DungeonCrewResumeState Capture(object crew) => new(
        (int)_native.Get(crew, "resumeDirectiveTarget")!, (float)_native.Get(crew, "resumeFleeDelay")!,
        (bool)_native.Get(crew, "resumeWithdrawing")!, (int)_native.Get(crew, "resumeRetreatOrigin")!,
        (float)_native.Get(crew, "resumeRecoveryProgress")!, (float)_native.Get(crew, "resumeDazedTime")!);
    internal void Restore(object crew, DungeonCrewResumeState saved, int compartmentCount)
    {
        if (!saved.Fits(compartmentCount)) throw new InvalidOperationException("Saved crew references unavailable compartments.");
        _native.Set(crew, "resumeDirectiveTarget", saved.DirectiveTarget); _native.Set(crew, "resumeFleeDelay", saved.FleeDelay);
        _native.Set(crew, "resumeWithdrawing", saved.Withdrawing); _native.Set(crew, "resumeRetreatOrigin", saved.RetreatOrigin);
        _native.Set(crew, "resumeRecoveryProgress", saved.RecoveryProgress); _native.Set(crew, "resumeDazedTime", saved.DazedTime);
    }
}
