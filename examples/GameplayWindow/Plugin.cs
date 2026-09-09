using System;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VGModAPI;
using VGModAPI.Unity;

namespace GameplayWindow;

[BepInPlugin(Id, "Gameplay window example", "1.0.0")]
[BepInDependency(ModApi.PluginId, "0.2.10")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Id = "vgmodapi.example.gameplay-window";
    private IGameplayUiService? _ui;
    private IHudRegistration? _launcher;
    private GameplayUiContainer? _container;
    private GameObject? _window;

    private void Awake()
    {
        _ui = ModApi.Services.GameplayUi;
        _launcher = ModApi.Services.Hud.Register(Id, "window", _ => ToggleWindow());
        _ui.Changed += UiChanged;
        Attach(_ui.Current); // Subscribe then query: also handles consumers loaded after UI readiness.
    }

    private void UiChanged(GameplayUiChange change)
    {
        if (change.Previous != null && ReferenceEquals(change.Previous, _container?.Host)) Detach();
        if (change.Current != null) Attach(change.Current);
    }

    private void Attach(GameplayUiSnapshot? host)
    {
        if (host == null || _ui == null || ReferenceEquals(host, _container?.Host)) return;
        // The expected host is checked by the API, including during reentrant notification delivery.
        var status = _ui.CreateContainer(host, Id, "windows", out var container);
        if (status != GameplayUiContainerStatus.Created)
        {
            Logger.LogDebug("Window host refused: " + status);
            return; // No retries, guessed timeout, singleton lookup or Harmony patch.
        }
        Detach(); _container = container;
        _launcher?.Update(new HudButton("Example window", HudCorner.TopRight, HudIcon.Storage,
            tooltip: "Toggle a consumer-owned window"), null);
    }

    private void ToggleWindow()
    {
        if (_container?.IsValid != true) return;
        if (_window != null) { _window.SetActive(!_window.activeSelf); return; }

        // Created later on player input, not during readiness. All layout/content belongs to this mod.
        var window = new GameObject("Example window", typeof(RectTransform), typeof(Image));
        try
        {
            var rect = (RectTransform)window.transform;
            rect.SetParent(_container.Root, false);
            window.layer = _container.Root.gameObject.layer;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(420, 160);
            window.GetComponent<Image>().color = new Color(0.04f, 0.08f, 0.14f, 0.96f);
            var labelObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            labelObject.layer = window.layer;
            var labelRect = (RectTransform)labelObject.transform;
            labelRect.SetParent(rect, false);
            labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(20, 20); labelRect.offsetMax = new Vector2(-20, -20);
            var label = labelObject.GetComponent<TextMeshProUGUI>();
            label.text = "This window belongs to the consumer mod.\nUse the shared HUD button to hide it.";
            label.fontSize = 22; label.color = Color.white; label.raycastTarget = false;
            _window = window;
        }
        catch { window.SetActive(false); Destroy(window); throw; }
    }

    private void Detach()
    {
        if (_ui?.Availability.Reason != ServiceUnavailableReason.ApiStopped) _launcher?.Update(null, null);
        _window = null; // Disposing the container also destroys every child, including this window.
        var container = _container; _container = null; container?.Dispose();
    }

    private void OnDestroy()
    {
        if (_ui != null) _ui.Changed -= UiChanged;
        // API shutdown may already have disposed HUD registrations; do not update them during unload.
        var launcher = _launcher; _launcher = null;
        Detach(); launcher?.Dispose(); _ui = null;
    }
}
