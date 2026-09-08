using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine.UI;

namespace VGModAPI.Runtime;

internal sealed class CraftingCommandUiRefresh
{
    private readonly Assembly _assembly;
    private readonly Dictionary<string, MethodInfo> _methods;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal CraftingCommandUiRefresh(Assembly assembly, Dictionary<string, MethodInfo> methods) { _assembly = assembly; _methods = methods; }
    internal void Refresh(object? station)
    {
        var current = Static("Source.Galaxy.POI.SpaceStation", "current");
        if (station != null && !ReferenceEquals(current, station)) return;
        var player = Static("Source.Player.GamePlayer", "current"); if (player == null) return;
        var forge = Static("Behaviour.UI.Forge.ForgeUI", "current");
        if (Active(forge))
        {
            ToggleValue(Member(forge!, "cargoToggle"), Convert.ToBoolean(Member(player, "forgeDepositInCargo")));
            _methods["commandRefreshForge"].Invoke(forge, Array.Empty<object>());
        }
        var refinery = Static("Behaviour.UI.Refinery.RefineryUI", "current");
        if (Active(refinery))
        {
            var settings = Member(refinery!, "settings");
            if (Active(settings) && current != null)
            {
                var nativeRefinery = Member(current, "refinery");
                var autoRefine = nativeRefinery != null && Convert.ToBoolean(Member(nativeRefinery, "autoRefine"));
                ToggleValue(Member(settings!, "autoRefine"), autoRefine);
                ToggleValue(Member(settings!, "autoSell"), Convert.ToBoolean(_methods["commandReadFlag"].Invoke(null, new object[] { "AutoSell", false })));
                if (Member(settings!, "autoRefineOptionShipCargo") is Toggle option && option != null) option.interactable = autoRefine;
            }
            // UpdateContent shows the job tab: never call it while the settings tab is selected.
            if (Active(Member(refinery!, "contents"))) _methods["commandRefreshRefinery"].Invoke(refinery, Array.Empty<object>());
        }
        var interior = Static("Behaviour.UI.Spacestation.SpaceStationInterior", "instance");
        if (Active(interior)) _methods["commandRefreshJobs"].Invoke(interior, Array.Empty<object>());
    }
    private static bool Active(object? value) => value is UnityEngine.Behaviour behaviour && behaviour != null && behaviour.isActiveAndEnabled;
    private static void ToggleValue(object? value, bool enabled) { if (value is Toggle toggle && toggle != null) toggle.SetIsOnWithoutNotify(enabled); }
    private static object? Member(object instance, string name) => RecipeCatalogNativeSource.Member(instance, name);
    private object? Static(string type, string member)
    {
        var native = _assembly.GetType(type, true)!;
        return native.GetField(member, Flags) is FieldInfo field ? field.GetValue(null) : native.GetProperty(member, Flags)!.GetValue(null);
    }
}
