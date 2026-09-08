using System;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Native scene and construction boundaries used by the recovery queue.</summary>
internal sealed class DungeonInitialRecoveryPorts
{
    internal Func<object, bool> ContainsWalkLocation = null!, IsLiveTarget = null!, HasLivePod = null!, SimulationReady = null!, ValidateOperation = null!;
    internal Func<string, object?> Resolve = null!;
    internal Func<DungeonOperationResumeState, object, object, object?, bool, object> Create = null!;
    internal Func<DungeonPodResumeState, object, object, object, object?, IDungeonReturnInstance> BuildPod = null!;
    internal Action<IDungeonReturnInstance, Guid> BindPod = null!;
    internal Action<object> Register = null!, Observe = null!;
    internal Action<object, Exception> Quarantine = null!;
}
