using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using VGModAPI.Core;
using VGModAPI.Runtime;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> ModInformationUi(Keyboard keyboard, Mouse mouse, Button entry, GameObject panel,
        Button row, Button check, Button automatic, TMP_Text details, Action<string> record, System.Text.StringBuilder images)
    {
        var events = EventSystem.current;
        events.SetSelectedGameObject(entry.gameObject);
        foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
        events.SetSelectedGameObject(row.gameObject);
        foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
        var apiPlugin = Chainloader.PluginInfos[ModApi.PluginId].Instance;
        var service = (ModUpdateService)AccessTools.Field(apiPlugin.GetType(), "_updates").GetValue(apiPlugin)!;
        var selected = ModApi.Mods!.Snapshot.Single(item => item.PluginId == Id);
        var release = panel.GetComponentsInChildren<Button>().Single(button => button.name == "Release link");
        var module = AccessTools.Field(apiPlugin.GetType(), "_modMenu").GetValue(apiPlugin)!;
        var lifetime = AccessTools.Field(module.GetType(), "_lifetime").GetValue(module)!;
        var view = AccessTools.Field(lifetime.GetType(), "_view").GetValue(lifetime)!;
        Require((RectTransform)AccessTools.Field(view.GetType(), "_panel").GetValue(view)! == panel.transform, "Wrong observed menu view.");
        var launcher = AccessTools.Field(view.GetType(), "_openUrl");
        var original = (Action<string>)launcher.GetValue(view)!;
        Require(original.Method.DeclaringType == typeof(Application) && original.Method.Name == "OpenURL", "Unexpected native URL launcher.");
        var launches = 0;
        var armed = false;
        Action<string> observe = url =>
        {
            Require(armed && url == ModUpdateChecks.Release && service.Status(selected).LastSuccess?.ReleaseUrl == url, "Unexpected or automatic browser destination.");
            ++launches;
            Require(launches == 1, "Duplicate browser activation.");
            var request = Path.Combine(_root!, "browser-launch-request.txt");
            using (var file = new FileStream(request + ".tmp", FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(file))
                writer.Write("mod-release-browser-v1\n" + ModUpdateChecks.Release + "\n");
            File.Move(request + ".tmp", request);
            original(url);
        };
        launcher.SetValue(view, observe);
        var background = Application.runInBackground;
        try
        {
            Require(!release.interactable && launches == 0, "Unchecked mod can launch a release.");
            events.SetSelectedGameObject(check.gameObject);
            foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
            Require(details.text.Contains("NETWORK CONFIRMATION") && service.Status(selected).State == ModUpdateState.NotChecked, "First click performed network I/O.");
            foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
            foreach (var frame in Wait(() => service.Status(selected).State == ModUpdateState.Available && release.interactable, "confirmed UI update")) yield return frame;
            Require(launches == 0 && details.text.Contains("Update available"), "Update result opened a browser or was not presented.");
            record("ui-confirmed-manual-without-automatic-browser");
            events.SetSelectedGameObject(automatic.gameObject);
            foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
            Require(!service.Automatic, "Automatic mode enabled without confirmation.");
            foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
            Require(service.Automatic, "Automatic confirmation did not enable the setting.");
            foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
            Require(!service.Automatic && launches == 0, "Automatic opt-out or browser isolation failed.");
            record("ui-confirmed-automatic-and-opt-out");
            Application.runInBackground = true;
            armed = true;
            foreach (var frame in MenuClick(mouse, release.transform)) yield return frame;
            Require(launches == 1, "Explicit release click did not invoke its launcher.");
            foreach (var frame in Wait(() => File.Exists(Path.Combine(_root!, "browser-launch.receipt")), "default browser destination verification")) yield return frame;
            Require(File.ReadAllLines(Path.Combine(_root!, "browser-launch.receipt")).FirstOrDefault() == "PASS", "Default browser verification failed.");
            record("ui-explicit-default-browser-release");
        }
        finally { Application.runInBackground = background; launcher.SetValue(view, original); }

        var canvas = entry.GetComponentInParent<Canvas>();
        var scaler = canvas.GetComponent<CanvasScaler>() ?? throw new InvalidOperationException("Native UI scaler missing.");
        var mode = scaler.uiScaleMode; var factor = scaler.scaleFactor;
        try
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize; scaler.scaleFactor = 1.25f;
            yield return null; yield return null;
            Canvas.ForceUpdateCanvases();
            Require(Math.Abs(canvas.scaleFactor - 1.25f) < .01f && panel.activeInHierarchy, "Scale change did not reach the active UI.");
            foreach (var frame in CaptureMenu("mod-menu-scale.png", images)) yield return frame;
        }
        finally { scaler.scaleFactor = factor; scaler.uiScaleMode = mode; }
        yield return null; yield return null;
        Require(scaler.uiScaleMode == mode && scaler.scaleFactor == factor, "UI scale settings were not restored.");
        record("ui-scale-restored");
        // Exercise the real adapter fault path only after all gameplay, not a fabricated supported hash.
        var adapter = (GameAdapter)AccessTools.Field(apiPlugin.GetType(), "_adapter").GetValue(apiPlugin)!;
        adapter.Guard(() => throw new InvalidOperationException("Controlled information qualification observer fault."));
        foreach (var frame in Wait(() => _api!.Capabilities.Any(capability => capability.Name == "session-lifecycle" && !capability.Available), "actual unavailable observer")) yield return frame;
        var diagnostic = panel.GetComponentsInChildren<Button>().Single(button => button.name == "Diagnostics");
        events.SetSelectedGameObject(diagnostic.gameObject);
        foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
        Require(details.text.Contains("Observer fault") && ModApi.Mods.Snapshot.Any(item => item.PluginId == ModApi.PluginId), "Unavailable API hid loader presence or diagnostics.");
        var scroll = panel.GetComponentsInChildren<ScrollRect>().Single(item => item.name == "Selected details");
        scroll.verticalNormalizedPosition = 0;
        yield return null; yield return null;
        foreach (var frame in CaptureMenu("mod-api-unavailable.png", images)) yield return frame;
        record("actual-api-unavailable-loader-presence");
        foreach (var frame in MenuKey(keyboard, Key.Escape)) yield return frame;
        Require(!panel.activeSelf && events.currentSelectedGameObject == entry.gameObject, "Full UI phase did not restore menu focus.");
    }
}
