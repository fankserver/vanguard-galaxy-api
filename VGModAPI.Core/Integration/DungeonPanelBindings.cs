namespace VGModAPI.Core;

internal static class DungeonPanelBindings
{
    internal const string Panel = "Behaviour.UI.Dungeon.DungeonPanel";
    internal static readonly MethodBinding[] Calls =
    {
        new("panelOpenLocation", Panel, "Open", false, "System.Void", BindingCatalog.BoardingLocation),
        new("panelOpenShip", Panel, "OpenForBoardable", false, "System.Void", BindingCatalog.Boardable),
        new("panelRect", Panel, "GetRectTransform", false, "UnityEngine.RectTransform")
    };
    internal static readonly MethodBinding[] Hooks =
    {
        new("panelOpenedLocation", Panel, "Open", false, "System.Void", BindingCatalog.BoardingLocation),
        new("panelOpenedShip", Panel, "OpenForBoardable", false, "System.Void", BindingCatalog.Boardable),
        new("panelClosed", Panel, "Close", false, "System.Void"),
        new("panelDisabled", Panel, "OnDisable", false, "System.Void")
    };
    internal static readonly (string Key, string Type, string Name, string ValueType)[] Members =
    {
        ("panelLocation", Panel, "_location", BindingCatalog.BoardingLocation),
        ("panelOperation", Panel, "_operation", BindingCatalog.BoardingOperation),
        ("panelButton", Panel, "startButton", "UnityEngine.UI.Button"),
        ("panelLabel", Panel, "infoLabel", "TMPro.TMP_Text")
    };
}
