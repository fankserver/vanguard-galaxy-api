using System;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledSettlementBindingTests
{
    [Fact]
    public void InspectedCaptureAndDefeatRetainNativeConsequences()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY") ?? throw new InvalidOperationException("Run make check-bindings."));
        var boardable = assembly.MainModule.GetType("Behaviour.Unit.BoardableUnit");
        var capture = Assert.Single(boardable.Methods, m => m.Name == "FinalizeCapture");
        var calls = capture.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Select(m => m.Name).ToArray();
        foreach (var name in new[] { "CapAmmoToOneMinute", "DamageModulesFromCapture", "DamageHullUpgrades", "ClearSimulation", "ClearNpcState", "PrepareForCapture", "AutoClaimCaptureMission", "LaunchCapturedShip" })
            Assert.Single(calls, called => called == name);
        Assert.Contains(calls, called => called == "IsNullOrEmpty");
        var fields = capture.Body.Instructions.Select(i => i.Operand).OfType<FieldReference>().Select(f => f.Name).ToArray();
        Assert.Contains("missionGuid", fields); Assert.Contains("capturedShips", fields); Assert.Contains("crewData", fields); Assert.Contains("faction", fields);
        var operation = assembly.MainModule.GetType("Behaviour.Dungeon.DungeonOperation");
        var defeat = Assert.Single(operation.Methods, m => m.Name == "HandleBoardingHostileVictory");
        var defeatCalls = defeat.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Select(m => m.Name).ToArray();
        Assert.Contains("FailBoardingMissions", defeatCalls); Assert.Contains("DestroyAllPods", defeatCalls); Assert.Contains("OnDefendersWon", defeatCalls);
        Assert.Contains(defeat.Body.Instructions, i => i.Operand is float value && value == 0.25f);
    }
}
