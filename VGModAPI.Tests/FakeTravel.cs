namespace Source.Galaxy
{
    public abstract class MapElement
    {
        public SystemMapData? system;
        private string? _name;
        public string guid { get; set; } = "native-id";
        public int NameReads { get; private set; }
        public string name { get { NameReads++; return _name ?? "generated"; } set { _name = value; } }
    }
    public sealed class SystemMapData : MapElement { }
    public class MapPointOfInterest : MapElement { }
}
namespace Source.Player
{
    public sealed partial class GamePlayer
    {
        public Source.Galaxy.SystemMapData? currentSystem;
        public Source.Galaxy.MapPointOfInterest? currentPointOfInterest;
        public double elapsedTime { get; set; }
        public Source.SpaceShip.SpaceShipData? currentSpaceShip { get; set; }
        public System.Collections.Generic.List<Source.Galaxy.MapPointOfInterest> waypoints = new();
        public Source.Galaxy.GalaxyMapData? map;
    }
}
namespace Source.SpaceShip.Auto
{
    public enum DockingState { Arriving, DockingAssigned, Docking, Docked, Undocking, Leaving, Flyby }
}
namespace Source.SpaceShip
{
    public sealed class SpaceShipData
    {
        public Source.SpaceShip.Auto.DockingState? dockingState;
    }
}
namespace Source.Galaxy.POI
{
    public sealed class SpaceStation : Source.Galaxy.MapPointOfInterest { }
}
namespace Behaviour.UI.Spacestation
{
    public sealed class SpaceStationInterior
    {
        public static SpaceStationInterior? instance;
        public Source.Galaxy.POI.SpaceStation? spacestation { get; set; }
    }
}
namespace Behaviour.Util
{
    public class Singleton<T> where T : class
    {
        protected static T? instance;
        public static T? SetTestInstance { set => instance = value; }
    }
}
namespace Behaviour.Managers
{
    public abstract class BasePoiManager
    {
        public Source.Galaxy.MapPointOfInterest? poi { get; set; }
        public bool initializedAndReady { get; set; }
    }
    public sealed class TestPoiManager : BasePoiManager { }
    public sealed class TravelManager
    {
        public BasePoiManager? localPoiManager { get; set; }
        public Source.Galaxy.MapPointOfInterest? targetPoi { get; set; }
        public Source.Galaxy.MapPointOfInterest? localTarget { get; set; }
        public bool usingJumpgate { get; set; }
        public bool isWarping { get; set; }
        public bool TravelActive() => false;
        public void TravelToNextWaypoint() { }
    }
}

namespace Source.Galaxy
{
    public sealed class GalaxyMapData
    {
        public static GalaxyMapData? current { get; set; }
        private readonly System.Collections.Generic.Dictionary<string, SystemMapData> _systems = new();
        private readonly System.Collections.Generic.Dictionary<string, MapPointOfInterest> _pois = new();
        public void AddSystem(SystemMapData s) => _systems[s.guid] = s;
        public void AddPoi(MapPointOfInterest p) => _pois[p.guid] = p;
        public SystemMapData? GetSystem(string guid) => _systems.TryGetValue(guid, out var s) ? s : null;
        public MapPointOfInterest? GetPointOfInterest(string guid) => _pois.TryGetValue(guid, out var p) ? p : null;
    }
}
namespace Source.Galaxy.POI
{
    // Mirror the game's public fields (not lazy name generation) used to resolve the real
    // nominal requested destination of a jump.
    public sealed class JumpGate : Source.Galaxy.MapPointOfInterest
    {
        public string? targetSystemGuid;
        public string? targetPoiGuid;
    }
    public sealed class Wormhole : Source.Galaxy.MapPointOfInterest { }
}
namespace Behaviour.Unit
{
    public sealed class SpaceShip
    {
        public Source.SpaceShip.SpaceShipData? spaceShipData { get; set; }
    }
}
namespace Behaviour.Spacestation.Docking
{
    public sealed class DockingOption
    {
        public Behaviour.Unit.SpaceShip? dockingSpaceship { get; set; }
    }
}
