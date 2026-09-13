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

    internal static object? ExamplePlugin()
        => Chainloader.PluginInfos.TryGetValue("vgmodapi.example.wormhole-world", out var info) ? info.Instance : null;

    internal static T? Field<T>(object target, string name) where T : class
        => target.GetType().GetField(name, Any)?.GetValue(target) as T;

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
        return manager != null && type.GetMethod("IsDialogueOpen", Any)!.Invoke(manager, null) is true;
    }

    internal static void AdvanceDialogue()
    {
        var (type, manager) = DialogueManager();
        if (manager != null && type.GetMethod("IsDialogueOpen", Any)!.Invoke(manager, null) is true)
            type.GetMethod("NextOrFinish", Any)!.Invoke(manager, null);
    }

    private static (Type Type, Component? Manager) DialogueManager()
    {
        var type = GameType("Behaviour.Dialogues.DialogueManager");
        return (type, Resources.FindObjectsOfTypeAll(type).OfType<Component>().FirstOrDefault());
    }

    internal static void Screenshot(string label)
    {
        var root = Environment.GetEnvironmentVariable("VGMODAPI_E2E_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var safe = new string(label.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        ScreenCapture.CaptureScreenshot(Path.Combine(root, (++_screenshot).ToString("00") + "-" + safe + ".png"));
    }

}
