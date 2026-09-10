using System;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

public sealed partial class Plugin
{
    private UnitProtectionService? _unitProtection;
    private Harmony? _unitProtectionHarmony;

    private void InstallUnitProtection(Assembly assembly)
    {
        _unitProtection ??= new UnitProtectionService(_hub!);
        try
        {
            var methods = UnitProtectionBindings.Validate(assembly);
            UnitProtectionPatches.Runtime = new UnitProtectionRuntime(assembly, _unitProtection, error => Logger.LogError(error));
            _unitProtectionHarmony = new Harmony(ModApi.PluginId + ".unit-protection");
            _unitProtectionHarmony.Patch(methods["protectDamage"],
                prefix: new HarmonyMethod(typeof(UnitProtectionPatches.Damage), "Prefix"),
                finalizer: new HarmonyMethod(typeof(UnitProtectionPatches.Damage), "Finalizer"));
            _unitProtection.SetAvailable(true);
        }
        catch (Exception error) { TeardownUnitProtection(); Logger.LogError(error); }
    }

    private void TeardownUnitProtection()
    {
        UnitProtectionPatches.Runtime = null;
        try { _unitProtectionHarmony?.UnpatchSelf(); }
        catch (Exception error) { Logger.LogError(error); }
        _unitProtectionHarmony = null;
        _unitProtection?.SetAvailable(false);
    }
}
