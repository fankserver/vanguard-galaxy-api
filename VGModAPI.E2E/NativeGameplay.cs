using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace VGModAPI.E2E;

/// <summary>Native live-game assertions and rendered Unity HUD input for E2E only.
/// Gameplay travel is requested through the public ModAPI service.</summary>
internal static class NativeGameplay
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Assembly GameAssembly => AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
    private static Type GameType(string name) => GameAssembly.GetType(name, true)!;

    private static int _screenshot;

    internal static object? PluginInstance(string id)
        => Chainloader.PluginInfos.TryGetValue(id, out var info) ? info.Instance : null;

    internal static object? ExamplePlugin()
        => PluginInstance("vgmodapi.example.pocket-worlds");

    internal static T? Field<T>(object target, string name) where T : class
        => target.GetType().GetField(name, Any)?.GetValue(target) as T;

    internal static object? GetField(object target, string name)
        => target.GetType().GetField(name, Any)?.GetValue(target);

    internal static object? Prop(object target, string name)
        => target.GetType().GetProperty(name, Any)?.GetValue(target);

    internal static bool ClickHudRow(string label)
    {
        var buttonType = Type.GetType("UnityEngine.UI.Button, UnityEngine.UI");
        var textType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro");
        if (buttonType == null || textType == null) return false;
        foreach (var value in Resources.FindObjectsOfTypeAll(buttonType))
        {
            if (value is not Component button || !button.gameObject.activeInHierarchy) continue;
            var textProperty = textType.GetProperty("text", Any)!;
            var matched = button.GetComponentsInChildren(textType, true)
                .Any(text => (textProperty.GetValue(text) as string)?.StartsWith(label, StringComparison.Ordinal) == true);
            if (!matched) continue;
            var eventSystemType = Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI")!;
            var eventDataType = Type.GetType("UnityEngine.EventSystems.BaseEventData, UnityEngine.UI")!;
            var eventSystem = eventSystemType.GetProperty("current", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
            var eventData = Activator.CreateInstance(eventDataType, eventSystem);
            button.GetType().GetMethod("OnSubmit", BindingFlags.Public | BindingFlags.Instance, null, new[] { eventDataType }, null)!
                .Invoke(button, new[] { eventData });
            return true;
        }
        return false;
    }

    internal static bool ClaimMissionRewards(IStoryMission mission)
    {
        var playerType = GameType("Source.Player.GamePlayer");
        var player = playerType.GetField("current", Any)?.GetValue(null);
        var nativeId = mission.NativeMissionId;
        if (player == null || string.IsNullOrEmpty(nativeId)) return false;
        var native = playerType.GetMethod("GetMission", Any)?.Invoke(player, new object[] { nativeId });
        if (native == null || native.GetType().GetMethod("CanClaimRewards", Any)?.Invoke(native, null) is not true) return false;
        var complete = playerType.GetMethods(Any).Single(method => method.Name == "CompleteMission"
            && method.GetParameters() is var parameters && parameters.Length == 2
            && parameters[0].ParameterType.IsInstanceOfType(native) && parameters[1].ParameterType == typeof(bool));
        complete.Invoke(player, new[] { native, (object)false });
        return true;
    }

    internal static object? Poi(string id)
    {
        var map = GameType("Source.Galaxy.GalaxyMapData").GetProperty("current", Any)!.GetValue(null);
        return map?.GetType().GetMethod("GetPointOfInterest", Any)?.Invoke(map, new object[] { id });
    }

    internal static string SystemId(object poi)
    {
        var system = poi.GetType().GetProperty("system", Any)?.GetValue(poi)
            ?? poi.GetType().GetField("system", Any)?.GetValue(poi)
            ?? throw new MissingMemberException(poi.GetType().FullName, "system");
        return system.GetType().GetField("guid", Any)?.GetValue(system) as string
            ?? system.GetType().GetProperty("guid", Any)?.GetValue(system) as string
            ?? throw new MissingMemberException(system.GetType().FullName, "guid");
    }

    internal static bool Bool(object target, string name)
        => (target.GetType().GetProperty(name, Any)?.GetValue(target)
            ?? target.GetType().GetField(name, Any)?.GetValue(target)) is true;

    internal static int StringListCount(object target, string name)
    {
        var value = target.GetType().GetField(name, Any)?.GetValue(target) as System.Collections.ICollection;
        return value?.Count ?? -1;
    }

    internal static bool DialogueOpen()
    {
        var (type, manager) = DialogueManager();
        return manager != null && DialogueContainerVisible(type, manager)
            && type.GetMethod("IsDialogueOpen", Any)!.Invoke(manager, null) is true;
    }

    internal static void AdvanceDialogue()
    {
        var (type, manager) = DialogueManager();
        if (manager != null && DialogueContainerVisible(type, manager)
            && type.GetMethod("IsDialogueOpen", Any)!.Invoke(manager, null) is true)
            type.GetMethod("NextOrFinish", Any)!.Invoke(manager, null);
    }

    private static (Type Type, Component? Manager) DialogueManager()
    {
        var type = GameType("Behaviour.Dialogues.DialogueManager");
        return (type, Resources.FindObjectsOfTypeAll(type).OfType<Component>()
            .FirstOrDefault(component => component.gameObject.activeInHierarchy));
    }

    private static bool DialogueContainerVisible(Type type, Component manager)
        => type.GetProperty("dialogueContainer", Any)?.GetValue(manager) is Component container
            && container.gameObject.activeInHierarchy;

    internal static string? Screenshot(string label)
    {
        var root = Environment.GetEnvironmentVariable("VGMODAPI_E2E_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(root)) return null;
        Directory.CreateDirectory(root);
        var safe = new string(label.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        var path = Path.Combine(root, (++_screenshot).ToString("00") + "-" + safe + ".png");
        ScreenCapture.CaptureScreenshot(path);
        return path;
    }

}
