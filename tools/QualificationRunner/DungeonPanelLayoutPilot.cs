using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private bool DungeonLayoutReady(string stage, bool ready, RectTransform anchor, List<string> records)
    {
        var root = GameObject.Find("Mod API dungeon contributions");
        var toggle = GameObject.Find("Dungeon mod actions toggle");
        var text = stage + " ready=" + ready + " screen=" + Screen.width + "x" + Screen.height
            + " native=" + anchor.rect + " position=" + anchor.position + " scale=" + anchor.lossyScale
            + " root=" + (root ? root.GetComponent<RectTransform>().rect.ToString() + "@" + root.transform.position : "hidden")
            + " toggle=" + (toggle ? toggle.GetComponent<RectTransform>().rect.ToString() + "@" + toggle.transform.position : "hidden");
        if (records.Count < 12 && !records.Contains(text)) { records.Add(text); WriteAtomic("dungeon-layout-diagnostic.txt", records); }
        return ready;
    }
    private IEnumerable<object?> DungeonCapture(string stem)
    {
        yield return new WaitForEndOfFrame();
        var path = Path.Combine(_root!, stem + ".png");
        Require(!File.Exists(path), "Refusing to overwrite dungeon layout capture.");
        ScreenCapture.CaptureScreenshot(path);
        foreach (var frame in Wait(() => File.Exists(path) && new FileInfo(path).Length > 0, "Dungeon layout capture")) yield return frame;
        using var hash = SHA256.Create();
        WriteAtomic(stem + ".txt", new[] { "sha256=" + BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant() });
    }
    private IEnumerable<object?> CheckDungeonPanelLayout(UnityEngine.Object nativePanel, IDungeonPanelService panel, Mouse mouse)
    {
        var anchor = (RectTransform)((Component)nativePanel).transform;
        var scale = anchor.localScale;
        var selection = EventSystem.current.currentSelectedGameObject;
        IDisposable? tail = null; var calls = 0; var records = new List<string>();
        try
        {
            tail = panel.RegisterAction(Id, "layout-tail", _ => new DungeonPanelAction("Dungeon scroll tail"), _ => calls++, 1000);
            foreach (var frame in Wait(() => DungeonProbeButton("Dungeon scroll tail")?.GetComponent<Image>().depth >= 0, "Dungeon tail row layout")) yield return frame;
            var scroll = GameObject.Find("Mod API dungeon contributions").GetComponent<ScrollRect>();
            Require(scroll.content.rect.height > scroll.viewport.rect.height * 2, "Long section did not overflow the viewport.");
            // Deliberately seed offscreen focus to exercise the production OnSelect scroll adapter.
            EventSystem.current.SetSelectedGameObject(DungeonProbeButton("Dungeon scroll tail")!.gameObject);
            foreach (var frame in Wait(() => DungeonPointerReady(DungeonProbeButton("Dungeon scroll tail")!.transform), "Selected tail becomes visible")) yield return frame;
            Require(scroll.content.anchoredPosition.y > 0, "Selection did not scroll the long content.");
            foreach (var frame in DungeonClick(mouse, DungeonProbeButton("Dungeon scroll tail")!.transform)) yield return frame;
            Require(calls == 1, "Scrolled tail click did not dispatch exactly once.");
            foreach (var frame in DungeonCapture("dungeon-panel-scrolled")) yield return frame;
            anchor.localScale = scale * 1.25f;
            foreach (var frame in Wait(() => DungeonLayoutReady("compact closed", GameObject.Find("Dungeon mod actions toggle") != null && GameObject.Find("Mod API dungeon contributions") == null, anchor, records), "Scaled compact drawer closed")) yield return frame;
            var openToggle = GameObject.Find("Dungeon mod actions toggle");
            Require(openToggle, "Compact drawer opening toggle unavailable.");
            foreach (var frame in DungeonClick(mouse, openToggle!.transform)) yield return frame;
            foreach (var frame in Wait(() => DungeonLayoutReady("compact open", DungeonProbeButton("Dungeon scroll tail") != null, anchor, records), "Scaled compact drawer opened")) yield return frame;
            EventSystem.current.SetSelectedGameObject(null);
            EventSystem.current.SetSelectedGameObject(DungeonProbeButton("Dungeon scroll tail")!.gameObject);
            foreach (var frame in DungeonClick(mouse, DungeonProbeButton("Dungeon scroll tail")!.transform)) yield return frame;
            Require(calls == 2, "Scaled drawer tail click did not dispatch exactly once.");
            foreach (var frame in DungeonCapture("dungeon-panel-scaled")) yield return frame;
            var closeToggle = GameObject.Find("Dungeon mod actions toggle");
            Require(closeToggle, "Compact drawer closing toggle unavailable.");
            foreach (var frame in DungeonClick(mouse, closeToggle!.transform)) yield return frame;
            foreach (var frame in Wait(() => DungeonLayoutReady("drawer closed", GameObject.Find("Mod API dungeon contributions") == null, anchor, records), "Compact drawer closes")) yield return frame;
            anchor.localScale = scale;
            foreach (var frame in Wait(() => DungeonLayoutReady("scale restored", GameObject.Find("Dungeon mod actions toggle") == null && DungeonProbeButton("Dungeon scroll tail") != null, anchor, records), "Normal layout restored")) yield return frame;
            EventSystem.current.SetSelectedGameObject(null);
            EventSystem.current.SetSelectedGameObject(DungeonProbeButton("Dungeon scroll tail")!.gameObject);
            foreach (var frame in DungeonClick(mouse, DungeonProbeButton("Dungeon scroll tail")!.transform)) yield return frame;
            Require(calls == 3, "Restored layout click did not dispatch exactly once.");
            Require(anchor.localScale == scale, "Native dungeon panel scale was not restored.");
        }
        finally
        {
            if (anchor) anchor.localScale = scale;
            tail?.Dispose();
            if (EventSystem.current) EventSystem.current.SetSelectedGameObject(selection ? selection : null);
        }
    }
}
