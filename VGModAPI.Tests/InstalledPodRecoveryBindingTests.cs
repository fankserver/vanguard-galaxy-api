using System;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledPodRecoveryBindingTests
{
    [Fact]
    public void DonorDispatchRebindsActionsWithoutSubtractingCrewAgain()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY") ?? throw new InvalidOperationException("Run make check-bindings."));
        var operation = assembly.MainModule.GetType("Behaviour.Dungeon.DungeonOperation");
        var dispatch = Assert.Single(operation.Methods, m => m.Name == "DispatchReinforcer");
        var calls = dispatch.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Assert.Contains(calls, m => m.Name == "SetTemporaryActions");
        Assert.Contains(calls, m => m.Name == ".ctor" && m.DeclaringType.FullName == "Source.SpaceShip.Auto.BoardingReinforcementActions");
        Assert.DoesNotContain(calls, m => m.Name is "SubtractCrewFromShip" or "CollectCombatCrew" or "SpawnEnemyPods");
        var request = Assert.Single(operation.Methods, m => m.Name == "CheckReinforcementRequest");
        var requestCalls = request.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Select(m => m.Name).ToArray();
        Assert.True(Array.IndexOf(requestCalls, "SubtractCrewFromShip") >= 0);
        Assert.True(Array.IndexOf(requestCalls, "SubtractCrewFromShip") < Array.IndexOf(requestCalls, "DispatchReinforcer"));
    }
    [Fact]
    public void ApproachResumeConstructorDoesNotDebitCrewOrSpawnPods()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY") ?? throw new InvalidOperationException("Run make check-bindings."));
        var operation = assembly.MainModule.GetType("Behaviour.Dungeon.DungeonOperation");
        var constructor = Assert.Single(operation.Methods, m => m.IsConstructor && !m.IsStatic &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "Behaviour.Unit.SpaceShip", "Behaviour.Unit.BoardableUnit", "Source.Dungeon.DungeonOptions", "System.Boolean", "System.Boolean" }));
        var calls = constructor.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Assert.All(calls, call => Assert.True(call.Name is ".ctor" or "get_data" or "get_location" or "set_isAutonomous" or "set_phase" || call.FullName.Contains("Behaviour.Dungeon.DungeonDefinition::Get("), call.FullName));
        var registration = Assert.Single(operation.Methods, m => m.Name == "RegisterReconstructedPod");
        var fields = registration.Body.Instructions.Select(i => i.Operand).OfType<FieldReference>().Select(f => f.Name).ToArray();
        Assert.Contains("_activePods", fields); Assert.Contains("_podsInFlight", fields);
        Assert.DoesNotContain("_pendingReinforcementPods", fields);
        Assert.DoesNotContain(registration.Body.Instructions.Where(i => i.OpCode.Code is Mono.Cecil.Cil.Code.Call or Mono.Cecil.Cil.Code.Callvirt).Select(i => i.Operand).OfType<MethodReference>(), m => m.Name is "AddCrew" or "AddAttackers" or "HandlePodCrewLanded");
    }
    [Fact]
    public void SettlementCarrierConstructorDoesNotStartCombatOrRegisterWithManager()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY") ?? throw new InvalidOperationException("Run make check-bindings."));
        var operation = assembly.MainModule.GetType("Behaviour.Dungeon.DungeonOperation");
        var constructor = Assert.Single(operation.Methods, m => m.IsConstructor && !m.IsStatic &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "Behaviour.Unit.SpaceShip", "Source.Data.Persistable.DungeonLocationData", "Source.Dungeon.DungeonOptions", "System.Boolean" }));
        var calls = constructor.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Assert.All(calls, call => Assert.True(call.Name is ".ctor" or "set_isAutonomous" or "set_phase" || call.FullName.Contains("Behaviour.Dungeon.DungeonDefinition::Get("), call.FullName));
        var writes = constructor.Body.Instructions.Where(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld).Select(i => ((FieldReference)i.Operand).Name).ToArray();
        foreach (var field in new[] { "ship", "location", "options", "_definition" }) Assert.Contains(writes, written => written == field || written == "<" + field + ">k__BackingField");
        Assert.DoesNotContain("simulation", writes);
        foreach (var name in new[] { "isAutonomous", "phase" })
        {
            var setter = Assert.Single(operation.Methods, m => m.Name == "set_" + name);
            Assert.Empty(setter.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>());
            Assert.Equal("<" + name + ">k__BackingField", Assert.Single(setter.Body.Instructions.Select(i => i.Operand).OfType<FieldReference>()).Name);
        }
        var returned = Assert.Single(operation.Methods, m => m.Name == "HandlePodCrewReturned");
        var returnCalls = returned.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Select(m => m.Name).ToArray();
        Assert.Contains("AccumulateCrewOverflow", returnCalls); Assert.Contains("JettisonOverflowCrew", returnCalls);
        Assert.DoesNotContain("FinalizeCapture", returnCalls); Assert.DoesNotContain("HandlePodSimulationComplete", returnCalls);
        var reads = returned.Body.Instructions.Select(i => i.Operand).OfType<FieldReference>().Select(f => f.Name).ToArray();
        Assert.DoesNotContain("boardableTarget", reads); Assert.DoesNotContain("simulation", reads);
    }
}
