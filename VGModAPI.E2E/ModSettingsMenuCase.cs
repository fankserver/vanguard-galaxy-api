using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VGModAPI.E2E;

internal static class ModSettingsMenuCase
{
    internal const string Id = "mod-settings-menu";
    private static string? _settingsScreenshot;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Button? FindButton(string text) => Resources.FindObjectsOfTypeAll<Button>().FirstOrDefault(button =>
        button != null && button.gameObject.activeInHierarchy && button.GetComponentsInChildren<TMP_Text>(true).Any(label => label.text == text));
    private static void Invoke(Button button)
    {
        var events = EventSystem.current ?? throw new InvalidOperationException("No EventSystem");
        button.OnSubmit(new BaseEventData(events));
    }
    private static string Rect(Button button)
    {
        var corners = new Vector3[4]; ((RectTransform)button.transform).GetWorldCorners(corners);
        var canvas = button.GetComponentInParent<Canvas>(); var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        var min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]); var max = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        return $"screen=({min.x:0},{min.y:0})-({max.x:0},{max.y:0}); resolution={Screen.width}x{Screen.height}; sibling={button.transform.GetSiblingIndex()}";
    }
    internal static IReadOnlyList<TestStep> Steps()
    {
        return new[]
        {
            new TestStep("main menu and examples load", "MainMenuUI.instance / Chainloader.PluginInfos", 45, () =>
                NativeSession.MenuReady() && NativeGameplay.PluginInstance(UiSurfacesCase.PluginId) != null
                    ? StepResult.Pass("menu and example ready") : StepResult.Wait("menu/example loading")),
            new TestStep("Mods button is visible inside the screen", "ModMenuView.Build / RectTransform", 20, () =>
            {
                var button = FindButton("Mods");
                if (button == null) return StepResult.Wait("active Mods button not found");
                var corners = new Vector3[4]; ((RectTransform)button.transform).GetWorldCorners(corners);
                var canvas = button.GetComponentInParent<Canvas>(); var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                var min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]); var max = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
                var receipt = Rect(button);
                Debug.Log("Mod menu E2E geometry: " + receipt);
                NativeGameplay.Screenshot("mods-button-main-menu");
                var visible = button.GetComponent<Graphic>()?.canvasRenderer.cull == false && min.x >= 0 && max.x <= Screen.width && min.y >= 0 && max.y <= Screen.height;
                return visible ? StepResult.Pass(receipt) : StepResult.Fail("Mods button outside/cull: " + receipt);
            }),
            TestStep.ActionThenWait("open Mods panel", "Mods Button.OnSubmit / ModMenuView.OpenPanel", 10,
                () => { Invoke(FindButton("Mods") ?? throw new InvalidOperationException("Mods button missing")); return StepResult.Pass("submitted"); },
                () => GameObject.Find("VGModAPI Mods panel")?.activeInHierarchy == true ? StepResult.Pass("panel visible") : StepResult.Wait("panel closed")),
            TestStep.ActionThenWait("select UI Surfaces mod", "Mod row / ModInformationPresenter", 10,
                () => { Invoke(FindButton("UI Surfaces example") ?? throw new InvalidOperationException("UI Surfaces row missing")); return StepResult.Pass("selected"); },
                () => FindButton("Settings")?.interactable == true ? StepResult.Pass("Settings available") : StepResult.Wait("settings disabled")),
            TestStep.ActionThenWait("open UI Surfaces settings", "Settings / ModSettingsPresenter", 10,
                () => { Invoke(FindButton("Settings") ?? throw new InvalidOperationException("Settings button missing")); return StepResult.Pass("submitted"); },
                () => FindButton("Reset to default")?.gameObject.activeInHierarchy == true && FindButton("Previous setting")?.gameObject.activeInHierarchy == true
                    ? StepResult.Pass("settings controls visible") : StepResult.Wait("settings controls hidden")),
            new TestStep("settings view renders published preference", "ModSettingsPresenter / TMP text", 10, () =>
            {
                var labels = Resources.FindObjectsOfTypeAll<TMP_Text>().Where(t => t != null && t.gameObject.activeInHierarchy).Select(t => t.text).ToArray();
                if (!labels.Any(t => t.Contains("Count window opens"))) return StepResult.Wait("published setting text absent");
                _settingsScreenshot = NativeGameplay.Screenshot("ui-surfaces-settings-menu");
                return StepResult.Pass("Count window opens rendered");
            }),
            new TestStep("settings screenshot persisted", "ScreenCapture.CaptureScreenshot", 10, () =>
                _settingsScreenshot == null || File.Exists(_settingsScreenshot)
                    ? StepResult.Pass(_settingsScreenshot ?? "capture disabled") : StepResult.Wait("capture pending")),
        };
    }
}
