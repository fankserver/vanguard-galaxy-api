using System;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledDungeonAegisBindingTests
{
    [Fact]
    public void EnterabilityMembersBindToInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        foreach (var spec in DungeonAegisBindings.Members)
        {
            var type = assembly.MainModule.GetType(spec.Type);
            Assert.NotNull(type);
            if (spec.Field)
            {
                var field = Assert.Single(type.Fields, value => value.Name == spec.Member);
                Assert.Equal(spec.Shape, field.FieldType.FullName); Assert.Equal(spec.Static, field.IsStatic);
                Assert.True(field.IsPublic);
            }
            else
            {
                var property = Assert.Single(type.Properties, value => value.Name == spec.Member);
                Assert.Equal(spec.Shape, property.PropertyType.FullName);
                Assert.Equal(spec.Static, property.GetMethod.IsStatic); Assert.Empty(property.Parameters);
            }
        }
        var simulation = assembly.MainModule.GetType("Source.Dungeon.DungeonSimulation");
        foreach (var name in new[] { "victoryAchieved", "isComplete" })
            Assert.Equal("System.Boolean", Assert.Single(simulation.Properties, p => p.Name == name).PropertyType.FullName);
        var poi = assembly.MainModule.GetType("Source.Galaxy.MapPointOfInterest");
        var persistables = Assert.Single(poi.Methods, m => m.Name == "GetPersistables" && m.Parameters.Count == 0);
        Assert.Contains("PersistableData", persistables.ReturnType.FullName);
        var manager = assembly.MainModule.GetType("Behaviour.Managers.DungeonManager");
        Assert.Contains("Singleton`1", manager.BaseType.FullName);
        Assert.Single(manager.Methods, m => m.Name == "GetOperation" && m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "Source.Data.Persistable.DungeonLocationData");
        var partData = assembly.MainModule.GetType("Source.Data.CombatStationPartData");
        Assert.Equal("Behaviour.Unit.CombatStationPart",
            Assert.Single(partData.Properties, p => p.Name == "partPrefab").PropertyType.FullName);
        var partType = assembly.MainModule.GetType("Behaviour.Unit.CombatStationPart").Properties.Single(p => p.Name == "partType");
        var names = assembly.MainModule.GetType(partType.PropertyType.FullName).Fields.Select(f => f.Name).ToArray();
        foreach (var docking in new[] { "DockingPad", "DockingTunnel", "CargoDock" }) Assert.Contains(docking, names);
        // The persisted invincibility flag is honored natively when station parts enter the world.
        var mark = assembly.MainModule.GetType("Source.Data.Persistable.DungeonLocationData");
        Assert.Contains(mark.Methods.Where(m => m.HasBody), method => method.Body.Instructions.Any(
            instruction => instruction.Operand is FieldReference field && field.Name == "stationIsInvincible"));
    }
}
