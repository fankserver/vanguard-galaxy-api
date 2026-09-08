using System;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledPodRecoveryBindingTests
{
    [Fact]
    public void ActiveResumeConstructorsRetainSimulationAndDoNotInitializeFreshCrew()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY") ?? throw new InvalidOperationException("Run make check-bindings."));
        var operation = assembly.MainModule.GetType("Behaviour.Dungeon.DungeonOperation");
        var constructors = operation.Methods.Where(method => method.IsConstructor && method.Parameters.Count == 3 && method.Parameters[2].ParameterType.FullName == "System.Boolean").ToArray();
        Assert.Equal(2, constructors.Length);
        foreach (var constructor in constructors)
        {
            var reads = constructor.Body.Instructions.Where(instruction => instruction.OpCode.Code == Mono.Cecil.Cil.Code.Ldfld).Select(instruction => (FieldReference)instruction.Operand).ToArray();
            Assert.Contains(reads, field => field.DeclaringType.FullName == "Source.Dungeon.DungeonData" && field.Name == "simulation");
            Assert.Contains(reads, field => field.DeclaringType.FullName == "Source.Dungeon.DungeonSimulation" && field.Name == "options");
            var calls = constructor.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().ToArray();
            Assert.DoesNotContain(calls, method => method.Name is "Initialize" or "AddCrew" or "AddAttackers" or "RemoveAssignedCrewFromShip" or "SpawnPods" or "BeginWalkSimulation");
            Assert.DoesNotContain(calls, method => method.Name == ".ctor" && method.DeclaringType.FullName == "Source.Dungeon.DungeonSimulation");
        }
    }
    [Fact]
    public void NativeDonorAbortResumesBehaviorWithoutCreditingReservedCrew()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY") ?? throw new InvalidOperationException("Run make check-bindings."));
        var actions = assembly.MainModule.GetType("Source.SpaceShip.Auto.BoardingReinforcementActions");
        var abort = Assert.Single(actions.Methods, method => method.Name == "ResumeAndAbort");
        Assert.Equal("Resume", Assert.Single(abort.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()).Name);
        var resume = Assert.Single(actions.Methods, method => method.Name == "Resume");
        var calls = resume.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().Select(method => method.Name).ToArray();
        Assert.Contains("SetTemporaryActions", calls); Assert.Contains("ResetAutoActions", calls);
        Assert.DoesNotContain("AddCrew", calls); Assert.DoesNotContain("SpawnEnemyPods", calls);
    }
    [Fact]
    public void ExtractionCompletionUsesCrewOverflowPathWithoutReplayingRewards()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY") ?? throw new InvalidOperationException("Run make check-bindings."));
        var operation = assembly.MainModule.GetType("Behaviour.Dungeon.DungeonOperation");
        var complete = Assert.Single(operation.Methods, method => method.Name == "CompleteExtraction");
        var calls = complete.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().ToArray();
        Assert.Single(calls, method => method.Name == "ReturnCrewToShip");
        Assert.Contains(calls, method => method.Name == "FinishOperation");
        Assert.DoesNotContain(calls, method => method.Name is "TransferLootToCargo" or "ApplyWalkOutcomeEffects" or "HandleSimulationComplete" or "TransferCapturedToBrig");
        var returned = Assert.Single(operation.Methods, method => method.Name == "ReturnCrewToShip");
        var returns = returned.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().Select(method => method.Name).ToArray();
        Assert.Contains("AccumulateCrewOverflow", returns); Assert.Contains("JettisonOverflowCrew", returns);
    }
    [Fact]
    public void OutboundWalkerAnimationDoesNotRemoveCrewBeforeSimulationEntry()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY") ?? throw new InvalidOperationException("Run make check-bindings."));
        var logistics = assembly.MainModule.GetType("Behaviour.Spacestation.LogisticsManager");
        var methods = logistics.Methods.Concat(logistics.NestedTypes.SelectMany(type => type.Methods)).Where(method => method.HasBody);
        var calls = methods.SelectMany(method => method.Body.Instructions).Select(instruction => instruction.Operand).OfType<MethodReference>().ToArray();
        Assert.DoesNotContain(calls, method => method.Name is "RemoveCrew" or "AddCrew" or "SubtractCrewFromShip");
        var operation = assembly.MainModule.GetType("Behaviour.Dungeon.DungeonOperation");
        var dispatch = Assert.Single(operation.Methods, method => method.Name == "HandleDockedCrewDispatch");
        Assert.Contains(dispatch.Body.Instructions.Select(instruction => instruction.Operand).OfType<FieldReference>(), field => field.Name == "_crewWalking");
        var begin = Assert.Single(operation.Methods, method => method.Name == "BeginWalkSimulation");
        Assert.Single(begin.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>(), method => method.Name == "RemoveAssignedCrewFromShip");
    }
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
