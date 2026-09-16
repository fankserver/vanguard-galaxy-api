using System;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

public sealed partial class Plugin
{
    private PickupPresentationService? _pickupPresentation;
    private Harmony? _pickupHarmony;
    private void InstallPickupPresentation(Assembly assembly)
    {
        _pickupPresentation ??= new PickupPresentationService(_hub!);
        try
        {
            var runtime = new PickupPresentationRuntime(assembly, _pickupPresentation, error => Logger.LogError(error));
            _pickupHarmony = new Harmony(ModApi.PluginId + ".pickup-presentation");
            PickupPresentationPatches.Runtime = runtime;
            _pickupHarmony.Patch(runtime.Notify,
                prefix: new HarmonyMethod(typeof(PickupPresentationPatches.Notify), "Prefix"),
                finalizer: new HarmonyMethod(typeof(PickupPresentationPatches.Notify), "Finalizer"));
            _pickupHarmony.Patch(runtime.Show,
                postfix: new HarmonyMethod(typeof(PickupPresentationPatches.Show), "Postfix"));
            _pickupPresentation.SetAvailable(true);
        }
        catch (Exception error) { TeardownPickupPresentation(); Logger.LogError(error); }
    }
    private void TeardownPickupPresentation()
    {
        PickupPresentationPatches.Runtime = null;
        try { _pickupHarmony?.UnpatchSelf(); }
        catch (Exception error) { Logger.LogError(error); }
        _pickupHarmony = null;
        _pickupPresentation?.SetAvailable(false);
    }
}
