using System;
namespace VGModAPI.Core;

internal enum DungeonPodParent { Donor, Target, Flight }
internal sealed class DungeonInitialPodPlacement
{
    internal DungeonPodParent Parent { get; }
    internal bool LocalPosition { get; }
    internal bool LocalRotation { get; }
    internal float X { get; }
    internal float Y { get; }
    internal float Angle { get; }
    internal bool PendingReinforcement { get; }
    internal DungeonInitialPodPlacement(DungeonPodResumeState saved)
    {
        if (saved.Transport is not { } transport || saved.Phase is not (DungeonPodPhase.Docked or DungeonPodPhase.Launching or DungeonPodPhase.Attached)) throw new InvalidOperationException("Initial transport state required.");
        Parent = saved.Phase == DungeonPodPhase.Docked ? DungeonPodParent.Donor : saved.Phase == DungeonPodPhase.Attached ? DungeonPodParent.Target : DungeonPodParent.Flight;
        LocalPosition = saved.Phase != DungeonPodPhase.Launching; LocalRotation = saved.Phase == DungeonPodPhase.Docked;
        X = transport.Pose[LocalPosition ? 3 : 0]; Y = transport.Pose[LocalPosition ? 4 : 1]; Angle = LocalRotation ? -90 : transport.Pose[2];
        PendingReinforcement = transport.PendingReinforcement && saved.Phase == DungeonPodPhase.Docked;
    }
}
