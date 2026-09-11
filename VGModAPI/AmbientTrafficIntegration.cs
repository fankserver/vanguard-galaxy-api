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
    private AmbientTrafficService? _ambientTraffic;
    private Harmony? _ambientTrafficHarmony;

    private void InstallAmbientTraffic(Assembly assembly)
    {
        _ambientTraffic ??= new AmbientTrafficService(_hub!);
        try
        {
            var methods = AmbientTrafficBindings.Validate(assembly);
            AmbientTrafficPatches.Runtime = new AmbientTrafficRuntime(assembly, _ambientTraffic, error => Logger.LogError(error));
            _ambientTrafficHarmony = new Harmony(ModApi.PluginId + ".ambient-traffic");
            _ambientTrafficHarmony.Patch(methods["trafficStationSpawn"],
                prefix: new HarmonyMethod(typeof(AmbientTrafficPatches.StationVisitor), "Prefix"));
            _ambientTrafficHarmony.Patch(methods["trafficGateSpawn"],
                prefix: new HarmonyMethod(typeof(AmbientTrafficPatches.GateTraffic), "Prefix"));
            _ambientTrafficHarmony.Patch(methods["trafficWormholeSpawn"],
                prefix: new HarmonyMethod(typeof(AmbientTrafficPatches.WormholeTraffic), "Prefix"));
            _ambientTrafficHarmony.Patch(methods["trafficSecurityPatrol"],
                prefix: new HarmonyMethod(typeof(AmbientTrafficPatches.SecurityPatrol), "Prefix"));
            _ambientTraffic.SetAvailable(true);
        }
        catch (Exception error) { TeardownAmbientTraffic(); Logger.LogError(error); }
    }

    private void TeardownAmbientTraffic()
    {
        AmbientTrafficPatches.Runtime = null;
        try { _ambientTrafficHarmony?.UnpatchSelf(); }
        catch (Exception error) { Logger.LogError(error); }
        _ambientTrafficHarmony = null;
        _ambientTraffic?.SetAvailable(false);
    }
}
