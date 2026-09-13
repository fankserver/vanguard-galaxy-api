using System;
using System.Linq;
using System.Reflection;

namespace VGModAPI.E2E;

/// <summary>Inspected native fixture setup, separate from public API assertions.
/// Uses new-game creation, never an existing save or internal API readiness bypass.</summary>
internal static class NativeSession
{
    private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
    private static Type GameType(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Single(a => a.GetName().Name == "Assembly-CSharp").GetType(name, true)!;
    private static FieldInfo Field(Type type, string name) => type.GetField(name)
        ?? throw new MissingFieldException(type.FullName, name);
    private static PropertyInfo Property(Type type, string name) => type.GetProperty(name,
        PublicStatic | BindingFlags.Instance) ?? throw new MissingMemberException(type.FullName, name);
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name)
        ?? throw new MissingMethodException(type.FullName, name);

    internal static bool MenuReady() => Field(GameType("Behaviour.UI.MainMenuUI"), "instance").GetValue(null) != null;

    internal static void Create()
    {
        // CreateTestArenaPlayer bypasses the new-player hook. Use the normal boundary,
        // then mirror MainMenuUI.StartTestArena's setup on that same player identity.
        var type = GameType("Source.Player.GamePlayer");
        Method(type, "CreateNewGamePlayer").Invoke(null, new object?[] { null, false });
        var player = Field(type, "current").GetValue(null)
            ?? throw new InvalidOperationException("CreateNewGamePlayer did not create a player.");
        Field(type, "isEphemeral").SetValue(player, true);
        RequireEphemeral();
        var commander = Property(type, "commander").GetValue(player)!;
        var icon = Method(GameType("Behaviour.Crew.OfficerIcons"), "GetRandom").Invoke(null, new object?[] { true, null });
        Method(commander.GetType(), "SetIcon").Invoke(commander, new[] { icon });
        Method(commander.GetType(), "SetName").Invoke(commander, new object[] { "E2E", "E2E", "Tester" });
        foreach (var name in new[] { "TestArena", "Default" })
            Method(type, "AddStoryteller").Invoke(player, new object?[] {
                Activator.CreateInstance(GameType("Source.Simulation.Story." + name), player), true });
        var managerType = GameType("Behaviour.GameManager");
        var manager = Property(managerType, "Instance").GetValue(null);
        Method(managerType, "StartNewGame").Invoke(manager, null);
    }

    internal static bool Initialized() => Property(GameType("GameplayManager"), "initialized").GetValue(null) is true;

    internal static void RequireEphemeral()
    {
        var type = GameType("Source.Player.GamePlayer");
        var player = Field(type, "current").GetValue(null);
        if (player == null || Field(type, "isEphemeral").GetValue(player) is not true)
            throw new InvalidOperationException("Refusing gameplay without a verified ephemeral player.");
    }
}
