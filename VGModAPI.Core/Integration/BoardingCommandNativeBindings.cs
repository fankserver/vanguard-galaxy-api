using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal interface IBoardingCommandNativeBindings
{
    object? Player { get; }
    object? Manager { get; }
    object? Get(object? obj, string key);
    object? Call(string key, object? target, params object[] arguments);
    object OutcomeReason(string name);
    bool ValidCrew(string id);
    object CreateOptions(BoardingCrewManifest crew, BoardingCommandOptions options);
    void ApplyOptions(object native, BoardingCommandOptions options);
}

internal interface IBoardingTacticalNativeBindings : IBoardingCommandNativeBindings
{
    void Set(object obj, string key, object? value);
    object EnumArgument(string key, int index, string name);
}

internal sealed class BoardingCommandNativeBindings : IBoardingTacticalNativeBindings
{
    private readonly Dictionary<string, List<MemberInfo>> _members = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, MethodInfo> _methods;
    private readonly FieldInfo _player, _manager;
    private readonly Type _options;
    internal BoardingCommandNativeBindings(GameBindings game, MethodBinding[]? additionalMethods = null,
        (string Key, string Type, string Name, string ValueType)[]? additionalMembers = null)
    {
        _methods = game.Resolve(BindingCatalog.Boarding.Concat(BindingCatalog.BoardingQueries).Concat(BoardingCommandBindings.Calls).Concat(additionalMethods ?? Array.Empty<MethodBinding>()).ToArray());
        _options = game.Assembly.GetType(BindingCatalog.BoardingOptions, true)!;
        if (_options.GetConstructor(Type.EmptyTypes) == null) throw new MissingMethodException(_options.FullName, ".ctor");
        _player = game.Assembly.GetType(BindingCatalog.Player, true)!.GetField("current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingFieldException(BindingCatalog.Player, "current");
        if (!NativeTypeName.Matches(_player.FieldType, BindingCatalog.Player)) throw new InvalidOperationException("Unexpected player field.");
        var managerType = game.Assembly.GetType(BindingCatalog.BoardingManager, true)!;
        var singleton = game.Assembly.GetType("Behaviour.Util.Singleton`1", true)!.MakeGenericType(managerType);
        _manager = singleton.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingFieldException(singleton.FullName, "instance");
        if (_manager.FieldType != managerType) throw new InvalidOperationException("Unexpected boarding manager field.");
        foreach (var spec in BoardingMembers.Schema.Select(s => (Key: s.Name, s.Type, s.Name, s.ValueType)).Concat(BoardingCommandMembers.Schema).Concat(additionalMembers ?? Array.Empty<(string Key, string Type, string Name, string ValueType)>()))
        {
            var type = game.Assembly.GetType(spec.Type, true)!;
            MemberInfo? member = type.GetField(spec.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            member ??= type.GetProperty(spec.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var valueType = member is FieldInfo f ? f.FieldType : (member as PropertyInfo)?.PropertyType;
            if (valueType == null || !NativeTypeName.Matches(valueType, spec.ValueType) || member is PropertyInfo p && (p.GetMethod == null || p.GetIndexParameters().Length != 0))
                throw new MissingMemberException(spec.Type, spec.Name);
            if (!_members.TryGetValue(spec.Key, out var list)) _members.Add(spec.Key, list = new List<MemberInfo>());
            list.Add(member!);
        }
    }
    public object? Player => _player.GetValue(null);
    public object? Manager => _manager.GetValue(null);
    private MemberInfo Member(object obj, string key) => _members[key].First(m => m.DeclaringType!.IsInstanceOfType(obj));
    public object? Get(object? obj, string key)
    {
        if (obj == null) return null;
        var member = Member(obj, key);
        return member is FieldInfo f ? f.GetValue(obj) : ((PropertyInfo)member).GetValue(obj);
    }
    public void Set(object obj, string key, object? value)
    {
        var field = Member(obj, key) as FieldInfo ?? throw new InvalidOperationException("Writable native field required.");
        if (field.IsInitOnly) throw new InvalidOperationException("Readonly native field.");
        if (field.FieldType.IsEnum && value is string name) value = Enum.Parse(field.FieldType, name, false);
        field.SetValue(obj, value);
    }
    public object? Call(string key, object? target, params object[] arguments)
    {
        try { return _methods[key].Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    public object EnumArgument(string key, int index, string name) => Enum.Parse(_methods[key].GetParameters()[index].ParameterType, name, false);
    public object OutcomeReason(string name) => Enum.Parse(_methods["commandRetreat"].GetParameters()[0].ParameterType, name, false);
    public bool ValidCrew(string id) => (bool)Call("commandCrewType", null, id, null!)!;
    public object CreateOptions(BoardingCrewManifest crew, BoardingCommandOptions options)
    {
        var native = Activator.CreateInstance(_options)!;
        Set(native, "assignedCrew", new Dictionary<string, int>(crew.Crew, StringComparer.Ordinal));
        ApplyOptions(native, options); return native;
    }
    public void ApplyOptions(object native, BoardingCommandOptions options)
    {
        Set(native, "ammunition", options.Ammunition.ToString()); Set(native, "stealth", options.Stealth.ToString());
        Set(native, "autoMove", options.AutoMove); Set(native, "buyout", options.AutoAcceptBuyout);
    }
}
