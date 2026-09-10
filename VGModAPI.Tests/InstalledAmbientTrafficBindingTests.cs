using System;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledAmbientTrafficBindingTests
{
    [Fact]
    public void DecorativeSpawnersAndMapIdentityBindToInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        foreach (var spec in AmbientTrafficBindings.Members)
        {
            var type = assembly.MainModule.GetType(spec.Type);
            Assert.NotNull(type);
            if (spec.Field)
            {
                var field = Assert.Single(type.Fields, value => value.Name == spec.Member);
                Assert.Equal(spec.Shape, field.FieldType.FullName); Assert.Equal(spec.Static, field.IsStatic);
            }
            else
            {
                var property = Assert.Single(type.Properties, value => value.Name == spec.Member);
                Assert.Equal(spec.Shape, property.PropertyType.FullName);
                Assert.Equal(spec.Static, property.GetMethod.IsStatic); Assert.Empty(property.Parameters);
            }
        }
        foreach (var spec in AmbientTrafficBindings.Methods)
        {
            var type = assembly.MainModule.GetType(spec.Type);
            var method = Assert.Single(type.Methods, value => value.Name == spec.Name &&
                value.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(spec.Parameters));
            Assert.Equal(spec.Static, method.IsStatic); Assert.Equal(spec.ReturnType, method.ReturnType.FullName);
            Assert.True(method.HasBody && method.IsPublic);
        }
        // Both spawners are periodic decorative "NPCSpawner" loops, not docking or payload delivery.
        foreach (var owner in new[] { "SpacestationExteriorManager", "Behaviour.Travel.JumpGateManager" })
            Assert.Single(assembly.MainModule.GetType(owner).NestedTypes,
                nested => nested.Name.Contains("NPCSpawner", StringComparison.Ordinal));
    }
}
