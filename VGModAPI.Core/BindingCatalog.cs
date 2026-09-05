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
    internal static readonly MethodBinding[] Session =
    {
        new("load", File, "LoadSaveGame", false, "System.Void"),
        new("loadRoutine", File, "LoadSaveGameStaged", false, "System.Collections.IEnumerator"),
        new("loadFailure", File, "HandleLoadFailure", false, "System.Void"),
        new("newPlayer", Player, "CreateNewGamePlayer", true, "System.Void", "Source.Player.PersonalHistoryData", "System.Boolean"),
        new("scenes", Scenes, "LoadScenesOnStartGame", false, "System.Void"),
        new("menu", Scenes, "StartMenu", false, "System.Void"),
        new("splash", Scenes, "SplashScreen", false, "System.Void"),
        new("gameplay", "GameplayManager", "Start", false, "System.Void")
    };
    internal static readonly MethodBinding[] Saves =
    {
        new("store", Save, "Store", true, "System.Void", "LightJson.JsonObject", "System.String", "Source.Util.SaveGameFormat", "System.Int32"),
        new("writeFile", Save, "WriteSaveFile", true, "System.Void", "System.IO.FileInfo", "LightJson.JsonObject", "Source.Util.SaveGameFormat"),
        new("writeMetadata", Save, "WriteVersionMetadata", true, "System.Void", "System.String", "System.String"),
        new("storeFailure", Save, "HandleStoreFailure", true, "System.Void", "LightJson.JsonObject", "System.String", "Source.Util.SaveGameFormat", "System.Int32", "System.IO.FileInfo", "System.Exception")
    };
}
