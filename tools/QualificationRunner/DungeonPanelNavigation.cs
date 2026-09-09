using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckDungeonPanelNavigation(IDungeonPanelApi panel)
    {
        var oldKeyboard = Keyboard.current; var oldGamepad = Gamepad.current;
        var oldSelection = EventSystem.current.currentSelectedGameObject;
        Keyboard? keyboard = null; Gamepad? gamepad = null;
        IDisposable? first = null; IDisposable? second = null;
        var firstCalls = 0; var secondCalls = 0;
        try
        {
            first = panel.RegisterAction(Id, "navigation-first", _ => new DungeonPanelAction("Navigation first"), _ => firstCalls++, -110);
            second = panel.RegisterAction(Id, "navigation-second", _ => new DungeonPanelAction("Navigation second"), _ => secondCalls++, -109);
            keyboard = InputSystem.AddDevice<Keyboard>(); gamepad = InputSystem.AddDevice<Gamepad>();
            foreach (var frame in Wait(() => DungeonProbeButton("Navigation second")?.GetComponent<UnityEngine.UI.Image>().depth >= 0, "Dungeon navigation rows")) yield return frame;
            // Focus is test setup; movement and submission below use the installed input module, not ExecuteEvents.
            EventSystem.current.SetSelectedGameObject(DungeonProbeButton("Navigation first")!.gameObject);
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.DownArrow)); yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState()); yield return null;
            foreach (var frame in Wait(() => EventSystem.current.currentSelectedGameObject == DungeonProbeButton("Navigation second")!.gameObject, "Keyboard dungeon navigation")) yield return frame;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter)); yield return null;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState()); yield return null;
            Require(secondCalls == 1 && firstCalls == 0, "Keyboard submit did not dispatch exactly once.");
            InputSystem.QueueStateEvent(gamepad, new GamepadState().WithButton(GamepadButton.DpadUp)); yield return null;
            InputSystem.QueueStateEvent(gamepad, new GamepadState()); yield return null;
            foreach (var frame in Wait(() => EventSystem.current.currentSelectedGameObject == DungeonProbeButton("Navigation first")!.gameObject, "Controller dungeon navigation")) yield return frame;
            InputSystem.QueueStateEvent(gamepad, new GamepadState().WithButton(GamepadButton.South)); yield return null;
            InputSystem.QueueStateEvent(gamepad, new GamepadState()); yield return null;
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
