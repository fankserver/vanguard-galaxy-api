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
    }
}
