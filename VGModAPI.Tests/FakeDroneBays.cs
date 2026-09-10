using System.Collections.Generic;

namespace Behaviour.Unit
{
    public class Drone : AbstractUnit
    {
        public string? DroneName;
        public static readonly Dictionary<string, Drone> TestCatalog = new();
        public static Drone? Get(string name) => TestCatalog.TryGetValue(name, out var drone) ? drone : null;
    }
}
namespace Behaviour.Equipment.Module
{
    public class DroneBayModule
    {
        public int _droneAmount;
        public int droneBonusAmount = 3;
        public bool shouldDeploy;
        public List<Behaviour.Unit.Drone> drones = new();
        public Behaviour.Unit.AbstractUnit? parent { get; set; }
        public float transitionDuration => 1.5f;
        public readonly List<int> AddedIndices = new();
        private void AddNewDrone(int idx)
        {
            AddedIndices.Add(idx);
            drones.Add(new Behaviour.Unit.Drone { DroneName = "built-" + idx });
        }
        private Behaviour.Unit.Drone? GetDronePrefab(int idx, Source.Data.AbstractUnitData? parent = null) => null;
    }
}
