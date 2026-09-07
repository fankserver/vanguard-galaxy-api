using System;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace VGModAPI.QualificationGuard;

// API-ABSENT consumer load control for the Echo arrival-snap soft dependency.
//
// It answers exactly one question: with VGModAPI and its Abstractions absent from the sandbox, does
// the SAME installed Echo build still load, keep its unrelated features patched, and leave its
// arrival-snap subscription unbound? It never references an API or Echo type: the plugin is reached
// through Chainloader and reflection only, exactly like the other consumer refusal checks here.
//
// It is NOT a claim that every Echo automation feature behaves correctly without the API. Plugin
// load and patch installation are recorded separately from actually observed hook invocations, and
// invocations only happen when the optional API-absent gameplay load control is selected too.
public sealed partial class Plugin
{
    private const string EchoPluginId = "vgecho";
    private const string EchoIdleHook = "Behaviour.Gameplay.IdleManager.Update";
    private static int _echoIdleInvocations;

    private bool EchoAbsentSelected => File.Exists(Path.Combine(_root!, "echo-absent.enabled"));

    /// <summary>Counts real native idle updates so an installed timing hook can be shown to JIT and
    /// run with the API absent, rather than only to be registered.</summary>
    private void InstallEchoAbsentCounter(Harmony harmony)
    {
        var idle = AccessTools.TypeByName("Behaviour.Gameplay.IdleManager");
        Require(idle != null, "Native IdleManager missing for the Echo API-absent control.");
        var update = AccessTools.Method(idle, "Update");
        Require(update != null, "Native IdleManager.Update missing for the Echo API-absent control.");
        harmony.Patch(update, postfix: new HarmonyMethod(typeof(Plugin), nameof(CountEchoIdleUpdate)));
    }

    private static void CountEchoIdleUpdate() => _echoIdleInvocations++;

    private void CheckEchoAbsent()
    {
        if (!EchoAbsentSelected) return;
        Require(_scenario == "MissingApi", "The Echo API-absent control only qualifies the MissingApi scenario.");
        Require(!Chainloader.PluginInfos.ContainsKey("vgmodapi"), "The API plugin is loaded; this is not an API-absent control.");
        var apiAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .Where(name => name == "VGModAPI" || name == "VGModAPI.Core" || name == "VGModAPI.Abstractions")
            .ToArray();
        Require(apiAssemblies.Length == 0, "API assemblies are loaded in an API-absent control: " + string.Join(", ", apiAssemblies));

        Require(Chainloader.PluginInfos.TryGetValue(EchoPluginId, out var echo) && echo.Instance != null,
            "The Echo consumer did not load with the API absent.");
        Require(echo!.Instance!.enabled, "The Echo consumer disabled itself with the API absent.");
        var version = echo.Metadata.Version.ToString(3);
        Require(version == "0.7.0", "The installed Echo consumer is " + version + ", not the pinned 0.7.0.");
        // The one feature that must be off, read from the consumer's own field.
        var subscription = AccessTools.Field(echo.Instance.GetType(), "_arrivalSnap");
        Require(subscription != null, "The Echo consumer has no _arrivalSnap field; its shape changed.");
        Require(subscription!.GetValue(echo.Instance) == null, "Echo bound an arrival-snap subscription with the API absent.");

        var owned = Harmony.GetAllPatchedMethods()
            .Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains(EchoPluginId) == true)
            .Select(method => (method.DeclaringType?.FullName ?? "") + "." + method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Require(owned.Contains(EchoIdleHook), "Echo's autopilot timing patch is absent with the API absent: ["
            + string.Join(" ", owned) + "]");
        Require(owned.Length >= 2, "Echo's unrelated feature patches are absent with the API absent: ["
            + string.Join(" ", owned) + "]");
        bool gameplay = File.Exists(Path.Combine(_root!, "vanilla-load.enabled"));
        if (gameplay)
            Require(_echoIdleInvocations > 0,
                "The native idle hook never executed during the API-absent gameplay load control.");
        File.WriteAllText(Path.Combine(_root!, "echo-absent.txt"),
            "PASS\nechoVersion=" + version + "\narrivalSnap=unbound\nownedPatches=" + owned.Length
            + "\ntimingHook=" + EchoIdleHook + "\nidleUpdateInvocations=" + _echoIdleInvocations
            + "\ngameplayLoadControl=" + gameplay
            + "\nEvidence of plugin load, patch installation and"
            + (gameplay ? " observed native hook invocation" : " NO observed invocation (no gameplay load control selected)")
            + " with VGModAPI absent. It is not a claim that every Echo automation feature behaves correctly without the API.\n");
    }
}
