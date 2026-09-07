using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Bootstrap;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> MenuLifecycleProbe(Keyboard keyboard, Mouse mouse, StringBuilder evidence)
    {
        var entry = GameObject.Find("VGModAPI Mods").GetComponent<Button>();
        var menu = entry.transform.parent.gameObject;
        var events = EventSystem.current;
        var nativeFont = menu.GetComponentsInChildren<TMP_Text>().First().font;
        events.SetSelectedGameObject(entry.gameObject);
        foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
        var panel = GameObject.Find("VGModAPI Mods panel");
        Require(panel != null, "Lifecycle probe could not open menu.");
        var confirmed = false;
        _expectedAlertKey = "VGModAPI isolated modal coexistence probe";
        try
        {
            Require(!AlertOpen() && Time.timeScale == 1f, "Modal probe requires the native unpaused menu.");
            AccessTools.Method(_alertType, "ShowMessage").Invoke(null, new object[] { _expectedAlertKey, "Confirm", (Action)(() => confirmed = true) });
            // Native AlertPopup.Start pauses on the following frame, not inside ShowMessage.
            yield return null; yield return null;
            Require(AlertOpen() && !panel!.activeSelf && !entry.interactable, "Mods did not yield to the native modal.");
            ModMenuModalChecks.AfterNativeStart(AlertOpen(), Time.timeScale);
            Require(!_alertCollision && _observedAlert is Component, "Native modal instance was not uniquely observed.");
            var popup = (Component)_observedAlert!;
            var confirm = popup.GetComponentsInChildren<Button>().Single(button => button.isActiveAndEnabled);
            foreach (var frame in MenuClick(mouse, confirm.transform)) yield return frame;
            foreach (var frame in Wait(() => !AlertOpen(), "native modal dismissal")) yield return frame;
            ModMenuModalChecks.AfterNativeDestroy(AlertOpen(), Time.timeScale);
            Require(confirmed && !panel!.activeSelf && entry.interactable, "Native modal did not dismiss without reopening Mods.");
            evidence.AppendLine("native-modal-coexistence=PASS native-time-scale-preserved=PASS no-forced-reopen=PASS");
        }
        finally { _expectedAlertKey = null; }

        var viewport = (RectTransform)panel!.transform.parent;
        var parent = viewport.parent;
        var sibling = viewport.GetSiblingIndex();
        var originalCanvas = viewport.GetComponentInParent<Canvas>();
        var replacement = new GameObject("VGModAPI qualification canvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        try
        {
            var canvas = replacement.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.scaleFactor = originalCanvas.scaleFactor;
            viewport.SetParent(canvas.transform, false);
            yield return null; yield return null;
            Require(panel == null, "Canvas replacement retained old panel.");
            Require(menu.GetComponentsInChildren<Button>().Count(button => button.name == "VGModAPI Mods") == 1, "Canvas replacement duplicated entry.");
            viewport.SetParent(parent, false);
            viewport.SetSiblingIndex(sibling);
            yield return null; yield return null;
            Require(menu.GetComponentsInChildren<Button>().Count(button => button.name == "VGModAPI Mods") == 1, "Canvas restoration duplicated entry.");
            Require(EventSystem.current == events && originalCanvas.isActiveAndEnabled, "Canvas replacement changed global input or disabled native canvas.");
            evidence.AppendLine("canvas-replacement-and-return=PASS single-entry=PASS");
        }
        finally
        {
            ProbeCleanup.Run(
                () => { if (viewport != null && parent != null) { viewport.SetParent(parent, false); viewport.SetSiblingIndex(sibling); } },
                () => { if (replacement != null && replacement.transform.childCount == 0) Object.Destroy(replacement); });
        }

        entry = GameObject.Find("VGModAPI Mods").GetComponent<Button>();
        events.SetSelectedGameObject(entry.gameObject);
        foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
        var width = Screen.width; var height = Screen.height; var mode = Screen.fullScreenMode;
        try
        {
            foreach (var frame in CaptureMenu("mod-menu-original.png", evidence)) yield return frame;
            Screen.SetResolution(1280, 720, FullScreenMode.Windowed);
            foreach (var frame in Wait(() => Screen.width == 1280 && Screen.height == 720, "1280x720 window resize")) yield return frame;
            foreach (var frame in CaptureMenu("mod-menu-1280.png", evidence)) yield return frame;
        }
        finally { Screen.SetResolution(width, height, mode); }
        foreach (var frame in Wait(() => Screen.width == width && Screen.height == height, "original resolution restoration")) yield return frame;
        Object.Destroy(Chainloader.PluginInfos[ModApi.PluginId].Instance);
        yield return null; yield return null;
        Require(!Resources.FindObjectsOfTypeAll<RectTransform>().Any(item => item.gameObject.scene.IsValid() &&
            (item.name == "VGModAPI Mods" || item.name == "VGModAPI Mods panel")), "API shutdown retained owned menu objects.");
        Require(ModApi.Mods == null && nativeFont != null && menu.activeInHierarchy && originalCanvas.isActiveAndEnabled && EventSystem.current == events,
            "Shutdown damaged shared native UI or retained the catalog.");
        evidence.AppendLine("api-shutdown-owned-ui-cleanup=PASS shared-native-ui-preserved=PASS");
    }

    private IEnumerable<object?> CaptureMenu(string name, StringBuilder evidence)
    {
        yield return null; yield return null;
        var panel = GameObject.Find("VGModAPI Mods panel");
        Require(panel != null && panel.activeInHierarchy, "Resize closed the panel unexpectedly.");
        var rect = (RectTransform)panel!.transform;
        var viewport = (RectTransform)rect.parent;
        Require(Math.Abs(rect.rect.width - viewport.rect.width) < 1 && Math.Abs(rect.rect.height - viewport.rect.height) < 1,
            "Resized panel no longer covers its viewport.");
        foreach (var button in panel.GetComponentsInChildren<Button>().Where(button => !button.name.StartsWith("Mod row ", StringComparison.Ordinal)))
        {
            var caption = button.GetComponentInChildren<TMP_Text>();
            caption.ForceMeshUpdate();
            Require(!caption.isTextTruncated && caption.GetPreferredValues(caption.text).x <= caption.rectTransform.rect.width,
                button.name + " caption is truncated at this resolution.");
        }
        var path = Path.Combine(_root!, name);
        Require(!File.Exists(path), "Refusing to overwrite screenshot evidence.");
        ScreenCapture.CaptureScreenshot(path);
        foreach (var frame in Wait(() => File.Exists(path) && new FileInfo(path).Length > 0, "native menu screenshot")) yield return frame;
        using var hash = SHA256.Create();
        var digest = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        evidence.AppendLine("screenshot=" + name + " resolution=" + Screen.width + "x" + Screen.height + " sha256=" + digest);
    }
}
