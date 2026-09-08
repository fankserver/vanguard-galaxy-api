namespace VGModAPI.Core;

internal static class DungeonSettlementBindings
{
    internal static readonly MethodBinding[] Hooks =
    {
        new("settlementTerminal", BindingCatalog.BoardingOperation, "HandlePodSimulationComplete", false, "System.Void", DungeonNativeSchema.Sim),
        new("settlementPrisonerScope", BindingCatalog.BoardingOperation, "TransferCapturedToBrig", false, "System.Void", DungeonNativeSchema.Sim),
        new("settlementPrisoners", "Source.SpaceShip.SpaceShipData", "AddPrisoners", false, "System.Int32", "System.String", "System.Int32"),
        new("settlementCrewSample", BindingCatalog.BoardingOperation, "Tick", false, "System.Void", "System.Single"),
        new("settlementLoot", BindingCatalog.BoardingOperation, "AddLootEntryToCargo", false, "System.Void", "Source.Item.Inventory", DungeonNativeSchema.Loot),
        new("settlementLootCount", "Behaviour.Dungeon.DungeonCompartmentLoot", "ResolveItemCount", true, "System.Int32", DungeonNativeSchema.Loot),
        new("settlementMasteryScope", BindingCatalog.BoardingOperation, "AddBoardingMasteryXp", false, "System.Void", "System.Single"),
        new("settlementMastery", "Source.SpaceShip.SpaceShipData", "AddMasteryExperience", false, "System.Void", "System.Single", "Source.Personnel.CommanderSpecialization")
    };
    internal static readonly (string Key, string Type, string Name, string ValueType)[] Members =
    {
        ("settlementMissionGuid", "Source.SpaceShip.SpaceShipData", "missionGuid", "System.String"),
        ("settlementCrewType", DungeonNativeSchema.Crew, "crewTypeId", "System.String"),
        ("settlementToken", BindingCatalog.BoardingLocation, "captureToken", "System.String"),
        ("settlementShipData", "Behaviour.Unit.SpaceShip", "spaceShipData", "Source.SpaceShip.SpaceShipData")
    };
}
