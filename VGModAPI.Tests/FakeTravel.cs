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
    }
}
