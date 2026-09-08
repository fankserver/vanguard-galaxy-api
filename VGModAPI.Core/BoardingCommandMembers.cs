namespace VGModAPI.Core;

internal static class BoardingCommandMembers
{
    private const string Options = BindingCatalog.BoardingOptions;
    internal static readonly (string Key, string Type, string Name, string ValueType)[] Schema =
    {
        ("hudBoardable", "Behaviour.UI.HUD.BoardingCancelButton", "_boardable", BindingCatalog.Boardable),
        ("panelLocation", "Behaviour.UI.Dungeon.DungeonPanel", "_location", BindingCatalog.BoardingLocation),
        ("playerShipData", BindingCatalog.Player, "currentSpaceShip", "Source.SpaceShip.SpaceShipData"),
        ("transponder", BindingCatalog.Player, "hasUmbralTransponder", "System.Boolean"),
        ("ship", "Source.SpaceShip.SpaceShipData", "spaceShip", "Behaviour.Unit.SpaceShip"),
        ("crewData", "Source.Data.AbstractUnitData", "crewData", "Source.Personnel.CrewData"),
        ("noRepLoss", "Source.Data.AbstractUnitData", "noReputationLoss", "System.Boolean"),
        ("crew", "Source.Personnel.CrewData", "crew", "System.Collections.Generic.Dictionary`2<System.String,System.Int32>"),
        ("capacity", "Behaviour.Unit.SpaceShip", "maxGrunts", "System.Int32"),
        ("destroyed", "Behaviour.Weapons.TargetableUnit", "isDestroyed", "System.Boolean"),
        ("dungeonData", BindingCatalog.BoardingLocation, "dungeonData", "Source.Dungeon.DungeonData"),
        ("savedSimulation", "Source.Dungeon.DungeonData", "simulation", "Source.Dungeon.DungeonSimulation"),
        ("retreating", "Source.Dungeon.DungeonSimulation", "isRetreating", "System.Boolean"),
        ("canExtract", "Source.Dungeon.DungeonSimulation", "canRequestExtraction", "System.Boolean"),
        ("operationShip", BindingCatalog.BoardingOperation, "ship", "Behaviour.Unit.SpaceShip"),
        ("ammunition", Options, "ammoType", "Source.Dungeon.AmmoType"),
        ("stealth", Options, "stealthMode", "Source.Dungeon.StealthMode"),
        ("buyout", Options, "autoAcceptBuyOut", "System.Boolean")
    };
}
