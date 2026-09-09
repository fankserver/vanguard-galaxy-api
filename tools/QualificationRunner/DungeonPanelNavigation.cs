using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private static IEnumerable<object?> DungeonGamepadKey(Gamepad gamepad, GamepadButton button)
    {
        InputSystem.QueueStateEvent(gamepad, new GamepadState().WithButton(button));
        yield return null; yield return null;
        InputSystem.QueueStateEvent(gamepad, new GamepadState());
        yield return null; yield return null;
    }
    private bool DungeonNavigationReady(string stage, bool ready, List<string> records)
    {
        if (records.Count >= 12) return ready;
        var module = EventSystem.current.currentInputModule;
        var input = module as InputSystemUIInputModule;
        var selected = EventSystem.current.currentSelectedGameObject;
        var record = stage + " ready=" + ready + " selected=" + (selected ? selected.GetComponentInChildren<TMPro.TMP_Text>()?.text : "none")
            + " module=" + module?.GetType().FullName + " enabled=" + (module && module.enabled)
            + " move=" + DungeonActionDiagnostic(input?.move?.action) + " submit=" + DungeonActionDiagnostic(input?.submit?.action)
            + " devices=" + string.Join("|", InputSystem.devices.Take(16).Select(device => device.name + ":" + device.enabled));
        if (!records.Contains(record)) { records.Add(record); WriteAtomic("dungeon-navigation-diagnostic.txt", records); }
        return ready;
    }
    private static string DungeonActionDiagnostic(InputAction? action) => action == null ? "none" : action.name + ":" + action.enabled
        + " bindings=" + string.Join("|", action.bindings.Take(16).Select(binding => binding.effectivePath))
        + " controls=" + string.Join("|", action.controls.Take(16).Select(control => control.path));
    private IEnumerable<object?> CheckDungeonPanelNavigation(IDungeonPanelApi panel)
    {
        var oldKeyboard = Keyboard.current; var oldGamepad = Gamepad.current;
        var oldSelection = EventSystem.current.currentSelectedGameObject;
        Keyboard? keyboard = null; Gamepad? gamepad = null;
        IDisposable? first = null; IDisposable? second = null;
        var firstCalls = 0; var secondCalls = 0;
        var diagnostics = new List<string>();
        try
        {
            first = panel.RegisterAction(Id, "navigation-first", _ => new DungeonPanelAction("Navigation first"), _ => firstCalls++, -110);
            second = panel.RegisterAction(Id, "navigation-second", _ => new DungeonPanelAction("Navigation second"), _ => secondCalls++, -109);
            keyboard = InputSystem.AddDevice<Keyboard>(); gamepad = InputSystem.AddDevice<Gamepad>();
            foreach (var frame in Wait(() => DungeonProbeButton("Navigation second")?.GetComponent<UnityEngine.UI.Image>().depth >= 0, "Dungeon navigation rows")) yield return frame;
            // Focus is test setup; movement and submission below use the installed input module, not ExecuteEvents.
            EventSystem.current.SetSelectedGameObject(DungeonProbeButton("Navigation first")!.gameObject);
            foreach (var frame in MenuKey(keyboard, Key.DownArrow)) yield return frame;
            foreach (var frame in Wait(() => DungeonNavigationReady("Keyboard move to second", EventSystem.current.currentSelectedGameObject == DungeonProbeButton("Navigation second")!.gameObject, diagnostics), "Keyboard dungeon navigation")) yield return frame;
            foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
            foreach (var frame in Wait(() => DungeonNavigationReady("Keyboard submit second", secondCalls == 1, diagnostics), "Keyboard dungeon submit")) yield return frame;
            Require(secondCalls == 1 && firstCalls == 0, "Keyboard submit did not dispatch exactly once.");
            foreach (var frame in DungeonGamepadKey(gamepad, GamepadButton.DpadUp)) yield return frame;
            foreach (var frame in Wait(() => DungeonNavigationReady("Controller move to first", EventSystem.current.currentSelectedGameObject == DungeonProbeButton("Navigation first")!.gameObject, diagnostics), "Controller dungeon navigation")) yield return frame;
            foreach (var frame in DungeonGamepadKey(gamepad, GamepadButton.South)) yield return frame;
            foreach (var frame in Wait(() => DungeonNavigationReady("Controller submit first", firstCalls == 1, diagnostics), "Controller dungeon submit")) yield return frame;
            Require(firstCalls == 1 && secondCalls == 1, "Controller submit did not dispatch exactly once.");
        }
        finally
        {
            first?.Dispose(); second?.Dispose();
            if (keyboard != null) InputSystem.RemoveDevice(keyboard);
            if (gamepad != null) InputSystem.RemoveDevice(gamepad);
            if (oldKeyboard != null && oldKeyboard.added) oldKeyboard.MakeCurrent();
            if (oldGamepad != null && oldGamepad.added) oldGamepad.MakeCurrent();
            if (EventSystem.current) EventSystem.current.SetSelectedGameObject(oldSelection ? oldSelection : null);
        }
    }
}
