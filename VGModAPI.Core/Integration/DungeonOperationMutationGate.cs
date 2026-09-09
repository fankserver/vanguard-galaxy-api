using System;

namespace VGModAPI.Runtime;

internal static class DungeonOperationMutationGate
{
    internal static bool Allows(object operation, IBoardingTacticalNativeBindings native, Func<object, bool>? simulationReady)
    {
        var simulation = native.Get(operation, "simulation");
        return simulation == null || simulationReady?.Invoke(simulation) != false;
    }
}
