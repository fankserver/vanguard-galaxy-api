using System;
using System.Linq;
using System.Reflection;

namespace EWTest;

/// <summary>Uses the game's native test-player entry, never loads an existing save.</summary>
internal static class FreshSession
{
    private static Type GameType(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Single(a => a.GetName().Name == "Assembly-CSharp").GetType(name, true)!;

    internal static bool TryStart()
    {
        var type = GameType("Behaviour.UI.MainMenuUI");
        var menu = type.GetField("instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        if (menu == null) return false;
        // Use the normal new-player boundary observed by ModAPI. The game's
        // CreateTestArenaPlayer bypasses that boundary entirely.
        var playerType = GameType("Source.Player.GamePlayer");
        playerType.GetMethod("CreateNewGamePlayer")!.Invoke(null, new object?[] { null, false });
        var player = playerType.GetField("current")!.GetValue(null)!;
        playerType.GetField("isEphemeral")!.SetValue(player, true);
        if (!IsEphemeral()) throw new InvalidOperationException("Refusing to start a non-ephemeral player");
        var commander = playerType.GetProperty("commander")!.GetValue(player)!;
        var icon = GameType("Behaviour.Crew.OfficerIcons").GetMethod("GetRandom")!.Invoke(null, new object?[] { true, null });
        commander.GetType().GetMethod("SetIcon")!.Invoke(commander, new[] { icon });
        commander.GetType().GetMethod("SetName")!.Invoke(commander, new object[] { "E2E", "E2E", "Tester" });
        var addStory = playerType.GetMethods().Single(m => m.Name == "AddStoryteller" && m.GetParameters().Length == 2);
        foreach (var name in new[] { "TestArena", "Default" })
            addStory.Invoke(player, new object?[] { Activator.CreateInstance(GameType("Source.Simulation.Story." + name), player), true });
        var managerType = GameType("Behaviour.GameManager");
        var manager = managerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)!.GetValue(null);
        managerType.GetMethod("StartNewGame", Type.EmptyTypes)!.Invoke(manager, null);
        return true;
    }

    internal static bool GameplayInitialized() =>
        GameType("GameplayManager").GetProperty("initialized")!.GetValue(null) is true;

    internal static bool IsEphemeral()
    {
        var type = GameType("Source.Player.GamePlayer");
        var player = type.GetField("current", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        return player != null && type.GetField("isEphemeral", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(player) is true;
    }
}
