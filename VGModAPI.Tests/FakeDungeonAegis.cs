using System.Collections.Generic;

namespace Source.Dungeon
{
    public class DungeonSimulation
    {
        public bool structuralCollapse { get; set; }
        public bool isRetreating { get; set; }
        public bool victoryAchieved { get; set; }
        public bool isComplete { get; set; }
    }
    public class DungeonData
    {
        public bool isOperationActive;
        public bool dockingDestroyed;
        public float facilityIntegrity = -1f;
        public DungeonSimulation? simulation;
    }
}
namespace Source.Data
{
    public class CombatStationPartData
    {
        public Behaviour.Unit.CombatStationPart? partPrefab { get; set; }
    }
}
namespace Source.Data.Persistable
{
    public sealed class CombatStationData : PersistableData
    {
        private readonly List<CombatStationPartData> parts = new();
        public IEnumerable<CombatStationPartData> stationParts => parts;
        public void AddTestPart(CombatStationPartData part) => parts.Add(part);
    }
    public sealed class DungeonLocationData : PersistableData
    {
        public bool stationIsInvincible;
        public Source.Dungeon.DungeonData? dungeonData;
        public CombatStationData? stationData;
    }
}
namespace Behaviour.Unit
{
    public enum CombatStationPartType { Room, Connector, DockingPad, DockingTunnel, CargoDock }
    public class CombatStationPart : Behaviour.Weapons.TargetableUnit
    {
        public CombatStationPartType partType { get; set; }
        public Source.Data.Persistable.DungeonLocationData? dungeonLocationData;
    }
}
namespace Behaviour.Managers
{
    public sealed class DungeonManager : Behaviour.Util.Singleton<DungeonManager>
    {
        public readonly HashSet<Source.Data.Persistable.DungeonLocationData> LiveOperations = new();
        public object? GetOperation(Source.Data.Persistable.DungeonLocationData location)
            => LiveOperations.Contains(location) ? new object() : null;
    }
}
