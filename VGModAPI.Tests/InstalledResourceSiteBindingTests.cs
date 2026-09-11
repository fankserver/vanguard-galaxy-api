using System;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledResourceSiteBindingTests
{
    [Fact]
    public void ResourceSiteMembersBindToInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        foreach (var spec in ResourceSiteBindings.Members)
        {
            var type = assembly.MainModule.GetType(spec.Type);
            Assert.NotNull(type);
            if (spec.Field)
            {
                var field = Assert.Single(type.Fields, value => value.Name == spec.Member);
                Assert.Equal(spec.Shape, field.FieldType.FullName);
                Assert.Equal(spec.Static, field.IsStatic);
            }
            else
            {
                var property = Assert.Single(type.Properties, value => value.Name == spec.Member);
                Assert.Equal(spec.Shape, property.PropertyType.FullName);
                Assert.Equal(spec.Static, property.GetMethod.IsStatic);
                Assert.Empty(property.Parameters);
            }
        }
        foreach (var spec in ResourceSiteBindings.Methods)
        {
            var type = assembly.MainModule.GetType(spec.Type);
            Assert.NotNull(type);
            var method = Assert.Single(type.Methods, value => value.Name == spec.Name &&
                value.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(spec.Parameters));
            Assert.Equal(spec.ReturnType, method.ReturnType.FullName);
            Assert.Equal(spec.Static, method.IsStatic);
            Assert.True(method.HasBody);
        }
        // The recipe's fixed inputs: parameterless POI/data constructors, the enum members the seam
        // parses by name, the exact-count AsteroidFieldData constructor, existing-faction lookup and
        // SeededRandom.Global with the RandomRange the deterministic-station pick uses.
        foreach (var name in new[] { ResourceSiteBindings.Salvage, ResourceSiteBindings.Mining, ResourceSiteBindings.SalvageData, ResourceSiteBindings.HazardFieldData })
            Assert.Contains(assembly.MainModule.GetType(name).Methods, m => m.IsConstructor && !m.HasParameters);
        Assert.Contains(assembly.MainModule.GetType(ResourceSiteBindings.HazardName).Fields, f => f.Name == "DamageInRadius");
        Assert.Contains(assembly.MainModule.GetType(ResourceSiteBindings.DamageType).Fields, f => f.Name == "Radiation");
        foreach (var member in new[] { "ResearchStation", "RelayStation", "IndustrialFacility" })
            Assert.Contains(assembly.MainModule.GetType(ResourceSiteBindings.DungeonType).Fields, f => f.Name == member);
        var asteroidType = assembly.MainModule.GetType(ResourceSiteBindings.AsteroidField);
        Assert.Contains(asteroidType.Methods, m => m.IsConstructor && m.Parameters.Select(p => p.ParameterType.FullName)
            .SequenceEqual(new[] { "System.Int32", "System.Single", "System.Single", ResourceSiteBindings.OreSet, ResourceSiteBindings.OreSet, "System.Single" }));
        var faction = assembly.MainModule.GetType(ResourceSiteBindings.Faction);
        Assert.Contains(faction.Fields, f => f.Name == "allFactions" && f.IsStatic);
        Assert.Contains(faction.Methods, m => m.Name == "Get" && m.IsStatic
            && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.String" }));
        var random = assembly.MainModule.GetType(ResourceSiteBindings.SeededRandom);
        Assert.Contains(random.Fields, f => f.Name == "Global" && f.IsStatic);
        Assert.Contains(random.Methods, m => m.Name == "RandomRange" && !m.IsStatic
            && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Int32", "System.Int32" }));
        var poiBase = assembly.MainModule.GetType(ResourceSiteBindings.Poi);
        Assert.Contains(poiBase.Methods, m => m.Name == "RollIsAbandoned" && m.IsStatic);
    }
}
