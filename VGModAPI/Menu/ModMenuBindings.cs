using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VGModAPI.Menu;

internal sealed class ModMenuBindings
{
    private readonly FieldInfo _instance, _continue, _version;
    private readonly PropertyInfo _modal;

    internal ModMenuBindings(Assembly assembly)
    {
        var menu = assembly.GetType("Behaviour.UI.MainMenuUI", true)!;
        _instance = Field(menu, "instance", menu, true);
        _continue = Field(menu, "continueGame", typeof(Button), false);
        _version = Field(menu, "versionNumber", typeof(TMP_Text), false);
        var popup = assembly.GetType("Behaviour.UI.AlertPopup", true)!;
        _modal = popup.GetProperty("IsOpen", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMemberException(popup.FullName, "IsOpen");
        if (_modal.PropertyType != typeof(bool) || _modal.GetMethod == null || !_modal.GetMethod.IsStatic || _modal.GetIndexParameters().Length != 0)
            throw new MissingMemberException(popup.FullName, "IsOpen shape");
    }

    private static FieldInfo Field(Type owner, string name, Type type, bool isStatic)
    {
        var field = owner.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        if (field == null || field.FieldType != type || field.IsStatic != isStatic) throw new MissingFieldException(owner.FullName, name);
        return field;
    }

    internal bool ModalOpen => (bool)_modal.GetValue(null)!;
    internal MonoBehaviour? ActiveMenu
    {
        get
        {
            var menu = _instance.GetValue(null) as MonoBehaviour;
            return menu != null && menu.isActiveAndEnabled ? menu : null;
        }
    }

    internal Button NativeButton(MonoBehaviour menu) => _continue.GetValue(menu) as Button
        ?? throw new InvalidOperationException("Native menu button missing.");
    internal TMP_Text NativeVersion(MonoBehaviour menu) => _version.GetValue(menu) as TMP_Text
        ?? throw new InvalidOperationException("Native menu text missing.");

    internal static RectTransform? Viewport(MonoBehaviour menu, Canvas canvas)
    {
        // The inspected Gameview is a direct Canvas child, not a fullscreen overlay.
        for (var parent = menu.transform.parent; parent != null && parent != canvas.transform; parent = parent.parent)
            if (parent.parent == canvas.transform && parent.name == "Gameview") return parent as RectTransform;
        return null;
    }
}
