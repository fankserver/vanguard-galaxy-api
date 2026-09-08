using System;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Core;
using Xunit;
namespace VGModAPI.Tests;
[Trait("Category", "InstalledGame")]
public sealed class InstalledDungeonPanelTests
{
    [Fact]
    public void PanelEstimateUsesTheRuleAwareNativeEstimateBoundary()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY") ?? throw new InvalidOperationException("Run make check-bindings."));
        var panel = assembly.MainModule.GetType(DungeonPanelBindings.Panel);
        var info = Assert.Single(panel.Methods, method => method.Name == "BuildBoardingInfoText");
        Assert.Contains(info.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>(), method => method.Name == "EstimateFromData" && method.DeclaringType.FullName == "Source.Dungeon.DungeonSimulation");
        Assert.Contains(BoardingRuleBindings.Hooks, binding => binding.Key == "estimate");
        foreach (var member in DungeonPanelBindings.Members)
        {
            var field = Assert.Single(panel.Fields, field => field.Name == member.Name);
            Assert.Equal(member.ValueType, field.FieldType.FullName);
        }
    }
}
