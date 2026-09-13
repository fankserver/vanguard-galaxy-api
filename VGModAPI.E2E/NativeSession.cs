using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace VGModAPI.E2E;

/// <summary>Inspected native fixture setup, separate from public API assertions. Drives the real
/// New Game wizard and its SaveInputs boundary; never loads an existing save or enters Test Arena.</summary>
internal static class NativeSession
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
    private static Type GameType(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Single(a => a.GetName().Name == "Assembly-CSharp").GetType(name, true)!;
    private static FieldInfo Field(Type type, string name) => type.GetField(name, Any)
        ?? throw new MissingFieldException(type.FullName, name);
    private static PropertyInfo Property(Type type, string name) => type.GetProperty(name, Any)
        ?? throw new MissingMemberException(type.FullName, name);
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, Any)
        ?? throw new MissingMethodException(type.FullName, name);

    internal static bool MenuReady() => Field(GameType("Behaviour.UI.MainMenuUI"), "instance").GetValue(null) != null;

    internal static void OpenNewGameWizard()
    {
        var menu = Field(GameType("Behaviour.UI.MainMenuUI"), "instance").GetValue(null)
            ?? throw new InvalidOperationException("Main menu is not ready.");
        Method(menu.GetType(), "StartGame").Invoke(menu, null);
    }

    /// <summary>Advances one real wizard step per frame. At the final step it uses the wizard's own
    /// SaveInputs, marks that just-created player ephemeral before scenes start, then enters gameplay.</summary>
    internal static bool AdvanceNewGameWizard()
    {
        var type = GameType("Behaviour.UI.Main.NewGame");
        var wizard = Resources.FindObjectsOfTypeAll(type).OfType<Component>()
            .SingleOrDefault(value => value.gameObject.activeInHierarchy);
        if (wizard == null) return false;
        // A normal sandbox new game is independent and does not impose tutorial mission locks.
        Method(type, "ChooseSandbox").Invoke(wizard, null);
        var step = (int)Field(type, "currentStep").GetValue(wizard)!;
        if (step < 5)
        {
            Method(type, "SubmitInput").Invoke(wizard, null);
            return false;
        }
        Method(type, "SaveInputs").Invoke(wizard, null);
        MarkCurrentEphemeral();
        var managerType = GameType("Behaviour.GameManager");
        var manager = Property(managerType, "Instance").GetValue(null);
        Method(managerType, "StartNewGame").Invoke(manager, null);
        return true;
    }

    internal static bool Initialized() => Property(GameType("GameplayManager"), "initialized").GetValue(null) is true;

    private static void MarkCurrentEphemeral()
    {
        var type = GameType("Source.Player.GamePlayer");
        var player = Field(type, "current").GetValue(null)
            ?? throw new InvalidOperationException("New Game wizard did not create a player.");
        Field(type, "isEphemeral").SetValue(player, true);
        RequireEphemeral();
    }

    internal static void RequireEphemeral()
    {
        var type = GameType("Source.Player.GamePlayer");
        var player = Field(type, "current").GetValue(null);
        if (player == null || Field(type, "isEphemeral").GetValue(player) is not true)
            throw new InvalidOperationException("Refusing gameplay without a verified ephemeral player.");
    }
}
