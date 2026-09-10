using System;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledEncounterBindingTests
{
    [Fact]
    public void EncounterMembersBindToInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        var module = assembly.MainModule;
        var poi = module.GetType("Source.Galaxy.MapPointOfInterest");
        var trigger = Assert.Single(poi.Methods, m => m.Name == "AddTriggeredSpawnFromFixedPayload");
        var parameters = trigger.Parameters;
        Assert.Equal("System.Single", parameters[0].ParameterType.FullName);
        Assert.Equal("System.String", parameters[1].ParameterType.FullName);
        Assert.Equal("System.Int32", parameters[2].ParameterType.FullName);
        Assert.Equal("Source.Galaxy.Faction", parameters[3].ParameterType.FullName);
        Assert.StartsWith("System.Nullable`1", parameters[6].ParameterType.FullName);
        // Every default beyond the seven authored arguments must carry a native default value,
        // because the seam fills them from ParameterInfo defaults.
        Assert.All(parameters.Skip(7), parameter => Assert.True(parameter.HasDefault));
        // The seam parses these enum members by name from the method's own parameter types.
        var loadout = ((GenericInstanceType)parameters[4].ParameterType).GenericArguments[0].Resolve();
        Assert.Contains(loadout.Fields, f => f.Name == "Combat");
        var rank = parameters[5].ParameterType.Resolve();
        foreach (var member in new[] { "Rookie", "Standard", "Veteran", "Elite", "Champion", "Commander", "Legendary" })
            Assert.Contains(rank.Fields, f => f.Name == member);
        // Hostility flags live on the unit-data base type; the seam checks instance type before writing.
        var unitData = module.GetType("Source.Data.AbstractUnitData");
        Assert.Contains(unitData.Fields, f => f.Name == "playerHostile" && f.FieldType.FullName == "System.Boolean");
        Assert.Contains(unitData.Fields, f => f.Name == "noReputationLoss" && f.FieldType.FullName == "System.Boolean");
    }
}
