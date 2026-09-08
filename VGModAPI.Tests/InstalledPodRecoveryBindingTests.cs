using System;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledPodRecoveryBindingTests
{
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
