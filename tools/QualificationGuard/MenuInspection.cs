using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace VGModAPI.QualificationGuard;

public sealed partial class Plugin
{
    // Opt-in read-only scene census. Not UI interaction or API qualification.
    private void InspectMenu()
    {
        Require(_scenario == "MissingApi", "Menu inspection requires the API-absent menu-only scenario.");
        Require(File.ReadAllText(Path.Combine(_root!, "menu-inspection.enabled")) == "menu-inspection-v1", "Unknown menu inspection selection.");
        var game = Assembly.Load("Assembly-CSharp");
        const string expected = "a2aad60bc68c31baccd636587d3c5ba4e651eacda59b0af42cd4f17f864284fb";
        using (var sha = SHA256.Create())
        using (var input = File.OpenRead(game.Location))
            Require(Hex(sha.ComputeHash(input)) == expected, "Uninspected menu assembly.");
        var type = game.GetType("Behaviour.UI.MainMenuUI", true)!;
        var menu = type.GetField("instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null) as Component;
        Require(menu != null && menu.gameObject.activeInHierarchy, "Active main menu missing.");
        var canvas = menu!.GetComponentInParent<Canvas>();
        Require(canvas != null, "Main menu canvas missing.");
        var output = new StringBuilder("menu-inspection-v1\n");
        output.AppendLine("gameSha256=" + expected);
        output.AppendLine("screen=" + Screen.width + "x" + Screen.height);
        output.AppendLine("canvas=" + PathOf(canvas!.transform) + " renderMode=" + canvas.renderMode + " scale=" + canvas.scaleFactor.ToString(CultureInfo.InvariantCulture));
        var alert = game.GetType("Behaviour.UI.AlertPopup", true)!;
        output.AppendLine("alertOpen=" + alert.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Static)!.GetValue(null));
        var ui = Assembly.Load("UnityEngine.UI");
        var eventType = ui.GetType("UnityEngine.EventSystems.EventSystem", true)!;
        var eventSystem = eventType.GetProperty("current", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        Require(eventSystem != null, "Menu EventSystem missing.");
        var module = eventType.GetProperty("currentInputModule")!.GetValue(eventSystem);
        Require(module != null, "Menu input module missing.");
        output.AppendLine("inputModule=" + module!.GetType().FullName);
        var transforms = canvas.GetComponentsInChildren<Transform>(true);
        Require(transforms.Length <= 2048, "Menu census exceeds its bound.");
        var fontCount = 0;
        foreach (var transform in transforms)
        {
            output.AppendLine("object=" + PathOf(transform) + " active=" + transform.gameObject.activeInHierarchy + " sibling=" + transform.GetSiblingIndex());
            if (transform is RectTransform rect)
                output.AppendLine("rect anchors=" + rect.anchorMin + "/" + rect.anchorMax + " pivot=" + rect.pivot + " size=" + rect.sizeDelta + " position=" + rect.anchoredPosition + " bounds=" + rect.rect);
            foreach (var component in transform.GetComponents<Component>())
            {
                if (component == null) continue;
                var componentType = component.GetType();
                output.AppendLine(" component=" + componentType.FullName);
                if (componentType.FullName == "UnityEngine.UI.CanvasScaler")
                    foreach (var property in new[] { "uiScaleMode", "referenceResolution", "screenMatchMode", "matchWidthOrHeight" })
                        output.AppendLine("  " + property + "=" + componentType.GetProperty(property)!.GetValue(component));
                if (componentType.FullName?.StartsWith("TMPro.", StringComparison.Ordinal) == true && componentType.GetProperty("font") != null)
                {
                    var font = componentType.GetProperty("font")!.GetValue(component) as UnityEngine.Object;
                    output.AppendLine("  font=" + (font == null ? "(none)" : Clean(font.name)) + " size=" + componentType.GetProperty("fontSize")!.GetValue(component));
                    if (font != null) ++fontCount;
                }
            }
            Require(output.Length <= 512 * 1024, "Menu census text exceeds its bound.");
        }
        Require(fontCount > 0, "No menu text font observed.");
        var bytes = new UTF8Encoding(false, true).GetBytes(output.ToString());
        Require(bytes.Length <= 1024 * 1024, "Menu census bytes exceed their bound.");
        File.WriteAllBytes(Path.Combine(_root!, "menu-inspection.txt"), bytes);
        using var hash = SHA256.Create();
        File.WriteAllText(Path.Combine(_root!, "menu-inspection.receipt"), "PASS\nmenu-inspection-v1\nsha256=" + Hex(hash.ComputeHash(bytes)) + "\n");
    }
    private static string Hex(byte[] bytes) => string.Concat(bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    private static string Clean(string name) => name.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    private static string PathOf(Transform transform)
    {
        var names = new System.Collections.Generic.List<string>();
        for (var current = transform; current != null; current = current.parent)
        { Require(names.Count < 128, "Menu transform ancestry exceeds its bound."); names.Add(Clean(current.name)); }
        names.Reverse(); return string.Join("/", names);
    }
}
