namespace VGModAPI.Core;

internal static class BoardingMembers
{
    private const string Location = BindingCatalog.BoardingLocation;
    private const string Op = BindingCatalog.BoardingOperation;
    private const string Sim = "Source.Dungeon.DungeonSimulation";
    private const string Room = "Source.CompartmentSystem.SimCompartmentData";
    private const string Crew = "Source.CompartmentSystem.SimCrewUnit";
    internal static readonly (string Type, string Name, string ValueType)[] Schema =
    {
        (BindingCatalog.Player, "credits", "System.Int64"),
        ("Source.Galaxy.MapPointOfInterest", "persistables", "System.Collections.Generic.List`1<Source.Data.Persistable.PersistableData>"),
        ("Source.Data.Persistable.TractorableItemData", "itemAmount", "System.Int32"),
        (BindingCatalog.Boardable, "data", Location),
        ("Behaviour.Unit.DungeonLocationUnit", "data", Location),
        (Location, "isEnterable", "System.Boolean"), (Location, "level", "System.Int32"),
        (Location, "isShipBased", "System.Boolean"), (Location, "shipTemplate", "System.String"),
        (Location, "shipData", "Source.SpaceShip.SpaceShipData"), (Location, "faction", "Source.Galaxy.Faction"),
        (Location, "dungeonType", "Source.Dungeon.DungeonType"), ("Source.Galaxy.Faction", "identifier", "System.String"),
        (Op, "location", Location), (Op, "boardableTarget", BindingCatalog.Boardable), (Op, "simulation", Sim),
        (Op, "phase", "Source.CompartmentSystem.MissionPhase"), (Op, "isComplete", "System.Boolean"),
        (Op, "_podsInFlight", "System.Int32"), (Op, "isAutonomous", "System.Boolean"), (Op, "options", BindingCatalog.BoardingOptions),
        (Op, "_activePods", "System.Collections.Generic.List`1<Behaviour.Persistables.BoardingPod>"),
        (BindingCatalog.BoardingOptions, "autoMove", "System.Boolean"),
        (BindingCatalog.BoardingOptions, "assignedCrew", "System.Collections.Generic.Dictionary`2<System.String,System.Int32>"),
        (Sim, "structureIntegrity", "System.Single"), (Sim, "maxStructureIntegrity", "System.Single"),
        (Sim, "awaitingPlayerExtraction", "System.Boolean"), (Sim, "isComplete", "System.Boolean"), (Sim, "victoryAchieved", "System.Boolean"),
        (Sim, "outcome", "Source.CompartmentSystem.MissionOutcome"),
        (Sim, "compartments", "System.Collections.Generic.List`1<Source.CompartmentSystem.SimCompartmentData>"),
        (Sim, "friendlyUnits", "System.Collections.Generic.List`1<Source.CompartmentSystem.SimCrewUnit>"),
        (Sim, "hostileUnits", "System.Collections.Generic.List`1<Source.CompartmentSystem.SimCrewUnit>"),
        (Room, "index", "System.Int32"), (Room, "type", "Source.CompartmentSystem.CompartmentType"),
        (Room, "state", "Source.CompartmentSystem.CompartmentState"), (Room, "isLocked", "System.Boolean"), (Room, "isDestroyed", "System.Boolean"),
        (Crew, "hp", "System.Int32"), (Crew, "compartmentIndex", "System.Int32"), (Crew, "state", "Source.CompartmentSystem.SimCrewState")
    };
}
