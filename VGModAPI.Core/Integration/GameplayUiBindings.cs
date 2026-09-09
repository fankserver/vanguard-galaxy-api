using System;
using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal static class GameplayUiBindings
{
    internal const string PanelType = "Behaviour.UI.Side_Menu.SidePanel";
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        (PanelType, "instance", PanelType, true, true)
    };
    internal static readonly MethodBinding[] Methods =
    {
        new("uiAwake", PanelType, "Awake", false, "System.Void"),
        new("uiStart", PanelType, "Start", false, "System.Void")
    };
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        var type = assembly.GetType(PanelType, true)!;
        var instance = type.GetField("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (instance == null || instance.FieldType != type)
            throw new MissingFieldException(PanelType, "instance");
        return new GameBindings(assembly).Resolve(Methods);
    }
}
