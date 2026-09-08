namespace VGModAPI.Core;

internal static class BoardingCommandBindings
{
    private const string Op = BindingCatalog.BoardingOperation;
    private const string Manager = BindingCatalog.BoardingManager;
    private const string Sim = "Source.Dungeon.DungeonSimulation";
    private const string Ship = "Behaviour.Unit.SpaceShip";
    private const string Manifest = "System.Collections.Generic.Dictionary`2<System.String,System.Int32>";
    internal static readonly MethodBinding[] Calls =
    {
        new("commandCrewType", "Behaviour.Crew.CrewType", "TryGet", true, "System.Boolean", "System.String", "Behaviour.Crew.CrewType&"),
        new("commandGetOperation", Manager, "GetOperation", false, Op, BindingCatalog.BoardingLocation),
        new("commandReinforce", Op, "AddReinforcements", false, "System.Void", Manifest),
        new("commandAbandon", Op, "Abandon", false, "System.Void"),
        new("commandRequestExtraction", Op, "RequestExtraction", false, "System.Void"),
        new("commandConfirmExtraction", Op, "ConfirmExtraction", false, "System.Void"),
        new("commandAutonomous", Op, "SetAutonomous", false, "System.Void", "System.Boolean"),
        new("commandRetreat", Sim, "TriggerRetreat", false, "System.Void", "Source.Dungeon.DungeonOutcomeReason"),
        new("commandNotifyCrew", "Source.Personnel.CrewData", "NotifyCrewChanged", true, "System.Void"),
        new("commandTravel", "Behaviour.Managers.TravelManager", "TravelActive", false, "System.Boolean"),
        new("commandLevelGap", Manager, "ExceedsLevelGap", true, "System.Boolean", "System.Int32"),
        new("commandFriendly", Manager, "IsFriendlyFaction", true, "System.Boolean", "Source.Galaxy.Faction")
    };
    internal static readonly MethodBinding[] Hooks =
    {
        new("commandSerialization", BindingCatalog.Save, "SaveCurrentState", true, "LightJson.JsonObject"),
        new("commandAutonomyHook", Op, "SetAutonomous", false, "System.Void", "System.Boolean"),
        new("commandRemoveAssigned", Op, "RemoveAssignedCrewFromShip", false, Manifest),
        new("commandBeginWalk", Op, "BeginWalkSimulation", false, "System.Void"),
        new("commandHudCancel", "Behaviour.UI.HUD.BoardingCancelButton", "OnPointerClick", false, "System.Void", "UnityEngine.EventSystems.PointerEventData"),
        new("commandUiStart", "Behaviour.UI.Dungeon.DungeonPanel", "OnStartClicked", false, "System.Void"),
        new("commandUiExtract", "Behaviour.UI.Dungeon.DungeonPanel", "OnExtractClicked", false, "System.Void"),
        new("commandUiReinforce", "Behaviour.UI.Dungeon.DungeonPanel", "AddReinforcements", false, "System.Void"),
        new("commandUiAmmo", "Behaviour.UI.Dungeon.DungeonPanel", "OnAmmoChanged", false, "System.Void", "System.Int32"),
        new("commandUiStealth", "Behaviour.UI.Dungeon.DungeonPanel", "OnStealthChanged", false, "System.Void", "System.Int32"),
        new("commandUiMove", "Behaviour.UI.Dungeon.DungeonPanel", "OnAutoMoveChanged", false, "System.Void", "System.Boolean")
    };
}
