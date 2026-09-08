using System;
using System.Collections.Generic;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class DungeonOperationOptionsAdapter
{
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly Func<object> _create;
    private readonly Func<string, string, object> _enum;
    internal DungeonOperationOptionsAdapter(IBoardingTacticalNativeBindings native, Func<object> create, Func<string, string, object> enumValue)
    { _native = native; _create = create; _enum = enumValue; }
    internal DungeonOperationOptions Capture(object options) => new(
        (IReadOnlyDictionary<string, int>)_native.Get(options, "assignedCrew")!,
        _native.Get(options, "ammunition")?.ToString() ?? "", _native.Get(options, "stealth")?.ToString() ?? "",
        (bool)_native.Get(options, "resumeAutoBuyOut")!, (bool)_native.Get(options, "resumeAutoMove")!, (int?)_native.Get(options, "resumePriority"));
    internal object Restore(DungeonOperationOptions saved)
    {
        var ammo = _enum("ammo", saved.Ammo); var stealth = _enum("stealth", saved.Stealth);
        var options = _create();
        _native.Set(options, "assignedCrew", new Dictionary<string, int>(saved.AssignedCrew, StringComparer.Ordinal));
        _native.Set(options, "ammunition", ammo); _native.Set(options, "stealth", stealth);
        _native.Set(options, "resumeAutoBuyOut", saved.AutoAcceptBuyOut); _native.Set(options, "resumeAutoMove", saved.AutoMove);
        _native.Set(options, "resumePriority", saved.PriorityCompartment); return options;
    }
}
