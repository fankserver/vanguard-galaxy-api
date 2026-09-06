using System.IO;

// Deliberately small reflection doubles; these do NOT simulate Unity scene scheduling.
namespace Source.Player
{
    public sealed class GamePlayer
    {
        public static GamePlayer? current;
        public bool isEphemeral;
        public System.Collections.Generic.List<Source.MissionSystem.Mission> missions = new();
        public System.Collections.Generic.List<string> missionsArchive = new();
        public Source.MissionSystem.Mission? currentBounty, currentPatrol, currentIndustry;
    }
}
namespace Source.Util
{
    public sealed class SaveGameFile
    {
        public readonly FileInfo File;
        public SaveGameFile(string path) => File = new FileInfo(path);
    }
    public static class SaveGame { public static string SavesPath = Path.GetTempPath(); }
}
public sealed class GameplayManager
{
    private readonly bool _initialized;
    public GameplayManager(bool initialized) => _initialized = initialized;
    public bool InitializedForTest => _initialized;
}
