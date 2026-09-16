using System;
using System.Reflection;
using System.Threading;

namespace VGModAPI.Runtime;

internal sealed class SkillTreeRuntime
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private readonly FieldInfo _player;
    private readonly PropertyInfo _commander, _maximumLevel, _identifier;
    private readonly MethodInfo _get, _name, _mastery;
    private readonly Type _specialization;
    internal SkillTreeRuntime(Assembly assembly)
    {
        var player = assembly.GetType("Source.Player.GamePlayer", true)!;
        var tree = assembly.GetType("Behaviour.Crew.Skilltree", true)!;
        _specialization = assembly.GetType("Source.Personnel.CommanderSpecialization", true)!;
        _player = player.GetField("current", Any) ?? throw new MissingFieldException(player.FullName, "current");
        _commander = player.GetProperty("commander", Any) ?? throw new MissingMemberException(player.FullName, "commander");
        _maximumLevel = assembly.GetType("Source.Util.GameMath", true)!.GetProperty("maxLevel", Any) ?? throw new MissingMemberException("GameMath.maxLevel");
        _identifier = tree.GetProperty("identifier", Any) ?? throw new MissingMemberException(tree.FullName, "identifier");
        _get = tree.GetMethod("Get", Any, null, new[] { typeof(string) }, null) ?? throw new MissingMethodException(tree.FullName, "Get");
        _name = assembly.GetType("Source.Personnel.SkillTreeData", true)!.GetMethod("GetSpecializationTreeName", Any, null, new[] { _specialization }, null) ?? throw new MissingMethodException("SkillTreeData.GetSpecializationTreeName");
        _mastery = tree.GetMethod("GetMasteryLevel", Any, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(tree.FullName, "GetMasteryLevel");
        if (_mastery.ReturnType != typeof(int) || _maximumLevel.PropertyType != typeof(int) || _identifier.PropertyType != typeof(string))
            throw new InvalidOperationException("Skill tree binding shape changed.");
    }
    private string Name(CommanderSpecialization specialization) => (string)_name.Invoke(null, new[] { Enum.Parse(_specialization, specialization.ToString()) })!;
    private bool HasCommander => _player.GetValue(null) is object player && _commander.GetValue(player) != null;
    internal SkillTree? Get(CommanderSpecialization specialization)
    {
        if (Thread.CurrentThread.ManagedThreadId != _thread || !HasCommander) return null;
        var tree = _get.Invoke(null, new object[] { Name(specialization) });
        return tree == null ? null : Read(tree);
    }
    internal SkillTree? Read(object tree)
    {
        if (Thread.CurrentThread.ManagedThreadId != _thread || !HasCommander) return null;
        var identifier = (string)_identifier.GetValue(tree)!;
        CommanderSpecialization? specialization = null;
        foreach (CommanderSpecialization value in Enum.GetValues(typeof(CommanderSpecialization)))
            if (Name(value) == identifier) { specialization = value; break; }
        return new SkillTree(identifier, specialization, (int)_mastery.Invoke(tree, null)!, (int)_maximumLevel.GetValue(null)!);
    }
}
