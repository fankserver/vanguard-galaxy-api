using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Native return bookkeeping without reopening combat or registering a ticking operation.</summary>
internal sealed class DungeonReturnCarrier
{
    private static readonly ConditionalWeakTable<object, object> Carriers = new();
    private readonly ConstructorInfo _operation, _location, _options;
    private readonly FieldInfo _pods, _dungeonType;
    private readonly MethodInfo _register;
    private readonly Type _shipType, _podType, _podDataType;
    internal DungeonReturnCarrier(Assembly assembly)
    {
        var operation = assembly.GetType(BindingCatalog.BoardingOperation, true)!;
        var location = assembly.GetType(BindingCatalog.BoardingLocation, true)!;
        var options = assembly.GetType(BindingCatalog.BoardingOptions, true)!;
        _shipType = assembly.GetType("Behaviour.Unit.SpaceShip", true)!;
        _podType = assembly.GetType(DungeonPodResumeBindings.Pod, true)!;
        _podDataType = assembly.GetType(DungeonPodResumeBindings.Data, true)!;
        _operation = operation.GetConstructor(new[] { _shipType, location, options, typeof(bool) }) ?? throw new MissingMethodException("Settlement carrier constructor unavailable.");
        _location = location.GetConstructor(Type.EmptyTypes) ?? throw new MissingMethodException("Detached location constructor unavailable.");
        _options = options.GetConstructor(Type.EmptyTypes) ?? throw new MissingMethodException("Options constructor unavailable.");
        _pods = location.GetField("boardingPods") ?? throw new MissingFieldException("Location pod collection unavailable.");
        _dungeonType = location.GetField("dungeonType") ?? throw new MissingFieldException("Location dungeon type unavailable.");
        if (!NativeTypeName.Matches(_pods.FieldType, "System.Collections.Generic.List`1<Source.Data.Persistable.BoardingPodData>") || !_dungeonType.FieldType.IsEnum)
            throw new InvalidOperationException("Unexpected return carrier schema.");
        _register = operation.GetMethod("RegisterReconstructedPod", new[] { _podType }) ?? throw new MissingMethodException("Pod registration unavailable.");
        if (_register.IsStatic || _register.ReturnType != typeof(void)) throw new InvalidOperationException("Unexpected pod registration signature.");
    }
    internal static bool IsSettlementOnly(object operation) => Carriers.TryGetValue(operation, out _);
    internal object Create(object recipientShip, string dungeonType, bool autonomous)
    {
        if (!_shipType.IsInstanceOfType(recipientShip)) throw new ArgumentException("Exact native recipient required.");
        var kind = Enum.Parse(_dungeonType.FieldType, dungeonType, false);
        if (!Enum.IsDefined(_dungeonType.FieldType, kind)) throw new ArgumentException("Unknown dungeon type.");
        var location = _location.Invoke(Array.Empty<object>()); _dungeonType.SetValue(location, kind);
        var options = _options.Invoke(Array.Empty<object>());
        var operation = _operation.Invoke(new[] { recipientShip, location, options, (object)autonomous });
        Carriers.Add(operation, location); return operation;
    }
    internal void Register(object operation, object pod, object data)
    {
        if (!Carriers.TryGetValue(operation, out var location) || !_podType.IsInstanceOfType(pod) || !_podDataType.IsInstanceOfType(data)) throw new ArgumentException("Invalid settlement carrier registration.");
        var pods = (IList)_pods.GetValue(location)!;
        if (pods.Contains(data)) throw new InvalidOperationException("Pod already registered on return carrier.");
        pods.Add(data);
        // Native subscription failures are not retried: a delegate may already have been installed.
        _register.Invoke(operation, new[] { pod });
    }
}
