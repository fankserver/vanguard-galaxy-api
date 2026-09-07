using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Bootstrap;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerator RunModMenuProbe()
    {
        Require(File.ReadAllText(Path.Combine(_root!, "mod-menu-probe.enabled")) == "mod-menu-probe-v1", "Invalid menu probe marker.");
        foreach (var frame in Wait(() => GameObject.Find("VGModAPI Mods") != null, "owned Mods entry")) yield return frame;
        Require(_api!.CurrentSession == null, "Menu probe must not enter gameplay.");
        var entry = GameObject.Find("VGModAPI Mods").GetComponent<Button>();
        var menu = entry.transform.parent.gameObject;
        var canvas = entry.GetComponentInParent<Canvas>();
        var events = EventSystem.current;
        Require(events != null, "Menu EventSystem missing.");
        var metadata = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Chainloader.PluginInfos[Id].Location)!, Id + ".vgmod.json"));
        var plugins = Path.GetFullPath(Path.Combine(_root!, "game", "BepInEx", "plugins")) + Path.DirectorySeparatorChar;
        Require(metadata.StartsWith(plugins, StringComparison.OrdinalIgnoreCase), "Probe metadata is outside the sandbox plugin directory.");
        Require(!File.Exists(metadata), "Refusing to overwrite existing probe metadata.");
        var oldKeyboard = Keyboard.current;
        Keyboard? keyboard = null;
        var oldMouse = Mouse.current;
        Mouse? mouse = null;
        var metadataCreated = false;
        var evidence = new StringBuilder("Native input-system menu probe v1\n");
        try
        {
            keyboard = InputSystem.AddDevice<Keyboard>();
            mouse = InputSystem.AddDevice<Mouse>();
            // Only the disposable driver's own metadata is changed, never another mod's identity.
            using (var file = new FileStream(metadata, FileMode.CreateNew, FileAccess.Write))
            {
                metadataCreated = true;
                using var writer = new StreamWriter(file);
                writer.Write("{\"schemaVersion\":1,\"pluginId\":\"" + Id + "\",\"description\":\"" +
                    string.Join(" ", Enumerable.Repeat("Long offline description for native scrolling verification.", 55)) + "\"}");
            }
            ModApi.Mods!.Refresh();
            events!.SetSelectedGameObject(entry.gameObject);
            foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
            var panel = GameObject.Find("VGModAPI Mods panel");
            Require(panel != null && panel.activeInHierarchy, "Keyboard submit did not open Mods.");
            var details = panel!.GetComponentsInChildren<ScrollRect>().Single(item => item.name == "Selected details");
            var list = panel.GetComponentsInChildren<ScrollRect>().Single(item => item.name == "Local mods");
            Require(panel.transform.parent.name == "Gameview", "Panel escaped the inspected viewport.");
            Require(panel.GetComponentsInChildren<TMP_Text>().All(text => !text.richText && !text.parseCtrlCharacters), "Unsafe rich text enabled.");
            evidence.AppendLine("keyboard-open=PASS plain-text=PASS viewport=Gameview");

            // Select the genuine loaded driver through its native row, not a presenter backdoor.
            var row = list.GetComponentsInChildren<Button>().Single(button => button.GetComponentInChildren<TMP_Text>().text.Contains("Controlled Qualification"));
            events.SetSelectedGameObject(row.gameObject);
            foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
            Require(details.content.rect.height > details.viewport.rect.height + 20, "Long metadata did not produce scrollable detail content.");
            events.SetSelectedGameObject(details.verticalScrollbar.gameObject);
            var before = details.content.anchoredPosition.y;
            foreach (var frame in MenuKey(keyboard, Key.DownArrow)) yield return frame;
            Require(events.currentSelectedGameObject == details.verticalScrollbar.gameObject, "Down arrow moved focus out of scrollbar.");
            Require(details.content.anchoredPosition.y > before + .1f, "Down arrow did not scroll native detail content.");
            foreach (var frame in MenuKey(keyboard, Key.UpArrow)) yield return frame;
            Require(details.content.anchoredPosition.y < before + .1f, "Up arrow did not reverse native scrolling.");
            foreach (var scroll in new[] { details, list })
            {
                var track = (RectTransform)scroll.verticalScrollbar.transform;
                var handle = scroll.verticalScrollbar.handleRect;
                Require(handle.rect.width > 0 && handle.rect.width <= track.rect.width + .1f && handle.rect.height > 0 && handle.rect.height <= track.rect.height + .1f,
                    "Scrollbar handle exceeds track geometry.");
                evidence.AppendLine(scroll.name + " handle=" + handle.rect.size + " track=" + track.rect.size);
            }
            evidence.AppendLine("keyboard-detail-scroll=PASS handle-geometry=PASS");
            foreach (var frame in MenuKey(keyboard, Key.Tab)) yield return frame;
            Require(events.currentSelectedGameObject != details.verticalScrollbar.gameObject && events.currentSelectedGameObject.transform.IsChildOf(panel.transform), "Tab did not leave scrollbar within panel.");

            var point = RectTransformUtility.WorldToScreenPoint(null, ((RectTransform)entry.transform).TransformPoint(((RectTransform)entry.transform).rect.center));
            var hits = new List<RaycastResult>();
            events.RaycastAll(new PointerEventData(events) { position = point }, hits);
            Require(hits.Count > 0 && hits[0].gameObject.transform.IsChildOf(panel.transform), "Panel permits raycast click-through to native menu.");
            var close = panel.GetComponentsInChildren<Button>().Single(button => button.name == "Close");
            foreach (var frame in MenuClick(mouse, close.transform)) yield return frame;
            Require(!panel.activeSelf && events.currentSelectedGameObject == entry.gameObject, "Pointer Close did not restore entry focus.");
            foreach (var frame in MenuKey(keyboard, Key.Enter)) yield return frame;
            Require(panel.activeSelf, "Menu could not reopen.");
            foreach (var frame in MenuKey(keyboard, Key.Escape)) yield return frame;
            Require(!panel.activeSelf && events.currentSelectedGameObject == entry.gameObject, "Escape did not close and restore focus.");
            evidence.AppendLine("tab=PASS pointer-close=PASS escape-focus=PASS raycast-barrier=PASS");

            menu.SetActive(false);
            yield return null; yield return null;
            Require(panel == null, "Inactive menu retained owned panel.");
            menu.SetActive(true);
            foreach (var frame in Wait(() => GameObject.Find("VGModAPI Mods") != null, "reattached Mods entry")) yield return frame;
            Require(menu.GetComponentsInChildren<Button>().Count(button => button.name == "VGModAPI Mods") == 1, "Duplicate Mods entry after reactivation.");
            Require(EventSystem.current == events && GameObject.Find("VGModAPI Mods").GetComponentInParent<Canvas>() == canvas, "Global UI infrastructure changed.");
            Require(_api.CurrentSession == null && !_events.Any(item => item.Kind == LifecycleEventKind.PlayerReady), "Menu probe entered gameplay.");
            evidence.AppendLine("inactive-teardown=PASS reattach-single-entry=PASS no-gameplay=PASS");
            var bytes = Encoding.UTF8.GetBytes(evidence.ToString());
            File.WriteAllBytes(Path.Combine(_root!, "mod-menu-probe.txt"), bytes);
            using var hash = SHA256.Create();
            var digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            File.WriteAllText(Path.Combine(_root!, "mod-menu-probe.receipt"), "PASS\nmod-menu-probe-v1\nsha256=" + digest + "\n");
            Passed("mod-menu-native-input");
        }
        finally
        {
            ProbeCleanup.Run(
                () => { if (menu != null) menu.SetActive(true); },
                () => { if (metadataCreated && File.Exists(metadata)) File.Delete(metadata); },
                () => ModApi.Mods?.Refresh(),
                () => { if (keyboard != null) InputSystem.RemoveDevice(keyboard); },
                () => { if (mouse != null) InputSystem.RemoveDevice(mouse); },
                () => oldKeyboard?.MakeCurrent(),
                () => oldMouse?.MakeCurrent());
        }
    }

    private static IEnumerable<object?> MenuKey(Keyboard keyboard, Key key)
    {
        InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
        yield return null; yield return null;
        InputSystem.QueueStateEvent(keyboard, new KeyboardState());
        yield return null; yield return null;
    }

    private static IEnumerable<object?> MenuClick(Mouse mouse, Transform target)
    {
        var rect = (RectTransform)target;
        var point = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
        InputSystem.QueueStateEvent(mouse, new MouseState { position = point });
        yield return null; yield return null;
        InputSystem.QueueStateEvent(mouse, new MouseState { position = point }.WithButton(MouseButton.Left));
        yield return null; yield return null;
        InputSystem.QueueStateEvent(mouse, new MouseState { position = point });
        yield return null; yield return null;
    }
}
