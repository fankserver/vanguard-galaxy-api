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
        Button row, Button check, TMP_Text details, Action<string> record, System.Text.StringBuilder images)
    {
        var events = EventSystem.current;
        events.SetSelectedGameObject(entry.gameObject);
        foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
        events.SetSelectedGameObject(row.gameObject);
        foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
        var apiPlugin = Chainloader.PluginInfos[ModApi.PluginId].Instance;
        var service = (ModUpdateService)AccessTools.Field(apiPlugin.GetType(), "_updates").GetValue(apiPlugin)!;
        var selected = ModApi.Services.Mods.Inventory.Entries.Single(item => item.PluginId == Id);
        var release = panel.GetComponentsInChildren<Button>(true).Single(button => button.name == "Release link");
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
            Require(Application.isFocused, "Owned game must have focus before the explicit browser action.");
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
            Require(service.Automatic && launches == 0, "Automatic checks must run without opening a browser.");
            // Use a successful, cooldown-expired entry: automatic refresh is not due for six hours.
            // A new completion here must therefore come from this single manual activation.
            foreach (var frame in Wait(() => service.Status(selected).State == ModUpdateState.Available &&
                service.Status(selected).RetryAt <= DateTimeOffset.UtcNow, "eligible manual refresh")) yield return frame;
            var previousCheck = service.Status(selected).CheckedAt;
            Require(previousCheck.HasValue && previousCheck.Value.AddHours(6) > DateTimeOffset.UtcNow.AddMinutes(1),
                "Automatic refresh is due; manual request evidence would be ambiguous.");
            events.SetSelectedGameObject(check.gameObject);
            foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
            Require(!details.text.Contains("NETWORK CONFIRMATION"), "Manual refresh requires confirmation.");
            Require(events.currentSelectedGameObject == check.gameObject, "Manual refresh lost keyboard focus.");
            foreach (var frame in Wait(() => service.Status(selected).State == ModUpdateState.Available && service.Status(selected).CheckedAt > previousCheck &&
                release.gameObject.activeInHierarchy, "new manual UI update result")) yield return frame;
            Require(launches == 0 && panel.GetComponentsInChildren<TMP_Text>().Single(text => text.name == "Update status").text.Contains("Update available") &&
                !details.text.Contains("Update available"), "Update result must appear separately from the description without opening a browser.");
            record("ui-immediate-refresh-without-automatic-browser");
            Require(service.Automatic && !panel.GetComponentsInChildren<Button>(true).Any(button => button.name == "Automatic updates"), "Automatic checking must not have a player toggle.");
            record("ui-automatic-checks-without-toggle");
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
        foreach (var frame in Wait(() => !_api!.SessionTracking.Availability.IsAvailable, "actual unavailable observer")) yield return frame;
        Require(!details.text.Contains("Observer fault") && ModApi.Services.Mods.Inventory.Entries.Any(item => item.PluginId == ModApi.PluginId), "Unavailable API must preserve the mod list without exposing technical faults in player details.");
        var scroll = panel.GetComponentsInChildren<ScrollRect>().Single(item => item.name == "Selected details");
        scroll.verticalNormalizedPosition = 0;
        yield return null; yield return null;
        foreach (var frame in CaptureMenu("mod-api-unavailable.png", images)) yield return frame;
        record("actual-api-unavailable-loader-presence");
        foreach (var frame in MenuKey(keyboard, Key.Escape)) yield return frame;
        Require(!panel.activeSelf && events.currentSelectedGameObject == entry.gameObject, "Full UI phase did not restore menu focus.");
    }
}
