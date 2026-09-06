using System;

namespace VGModAPI.Core;

internal sealed class MethodBinding
{
    internal readonly string Key, Type, Name, ReturnType;
    internal readonly bool Static;
    internal readonly string[] Parameters;
    internal MethodBinding(string key, string type, string name, bool isStatic, string returnType, params string[] parameters)
    { Key = key; Type = type; Name = name; Static = isStatic; ReturnType = returnType; Parameters = parameters; }
}

internal static class BindingCatalog
{
    internal const string InspectedSha256 = "a2aad60bc68c31baccd636587d3c5ba4e651eacda59b0af42cd4f17f864284fb";
    internal const string Save = "Source.Util.SaveGame";
    internal const string File = "Source.Util.SaveGameFile";
    internal const string Player = "Source.Player.GamePlayer";
    internal const string Scenes = "Behaviour.Bootstrap.SceneLoader";
    // Install tiny callees before callers: patching a caller can JIT it and inline
    // an as-yet-unpatched iterator factory, permanently bypassing that factory's hook.
    internal static readonly MethodBinding[] Session =
    {
        new("loadRoutine", File, "LoadSaveGameStaged", false, "System.Collections.IEnumerator"),
        new("loadFailure", File, "HandleLoadFailure", false, "System.Void"),
        new("newPlayer", Player, "CreateNewGamePlayer", true, "System.Void", "Source.Player.PersonalHistoryData", "System.Boolean"),
        new("scenes", Scenes, "LoadScenesOnStartGame", false, "System.Void"),
        new("menu", Scenes, "StartMenu", false, "System.Void"),
        new("splash", Scenes, "SplashScreen", false, "System.Void"),
        new("gameplay", "GameplayManager", "Start", false, "System.Void"),
        new("load", File, "LoadSaveGame", false, "System.Void")
    };
    internal const string Mission = "Source.MissionSystem.Mission";
    internal static readonly MethodBinding[] Missions =
    {
        new("missionArchive", Player, "ArchiveMission", false, "System.Void", "System.String", "System.Boolean"),
        new("missionRemove", Player, "RemoveMission", false, "System.Void", Mission, "System.Boolean"),
        new("missionStart", Mission, "OnMissionStart", false, "System.Void"),
        new("missionAccept", Player, "AddMissionWithLog", false, "System.Void", Mission, "System.Boolean"),
        new("missionClaim", Mission, "ClaimRewards", false, "System.Void", "System.Boolean"),
        new("missionFail", Mission, "MissionFailed", false, "System.Void", "System.String")
    };
    internal static readonly MethodBinding[] Saves =
    {
        new("writeFile", Save, "WriteSaveFile", true, "System.Void", "System.IO.FileInfo", "LightJson.JsonObject", "Source.Util.SaveGameFormat"),
        new("writeMetadata", Save, "WriteVersionMetadata", true, "System.Void", "System.String", "System.String"),
        new("storeFailure", Save, "HandleStoreFailure", true, "System.Void", "LightJson.JsonObject", "System.String", "Source.Util.SaveGameFormat", "System.Int32", "System.IO.FileInfo", "System.Exception"),
        new("store", Save, "Store", true, "System.Void", "LightJson.JsonObject", "System.String", "Source.Util.SaveGameFormat", "System.Int32")
    };
}
