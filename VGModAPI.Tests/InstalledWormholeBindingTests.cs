using System;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledWormholeBindingTests
{
    [Fact]
    public void NativeWormholePairMembersBindToInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        foreach (var spec in WormholePairBindings.Members)
        {
            var type = assembly.MainModule.GetType(spec.Type); Assert.NotNull(type);
            if (spec.Field)
            {
                var field = Assert.Single(type.Fields, f => f.Name == spec.Member);
                Assert.Equal(spec.Shape, field.FieldType.FullName); Assert.Equal(spec.Static, field.IsStatic);
            }
            else
            {
                var property = Assert.Single(type.Properties, p => p.Name == spec.Member);
                Assert.Equal(spec.Shape, property.PropertyType.FullName); Assert.Equal(spec.Static, property.GetMethod.IsStatic);
            }
        }
        foreach (var spec in WormholePairBindings.Methods)
        {
            var type = assembly.MainModule.GetType(spec.Type);
            var method = Assert.Single(type.Methods, m => m.Name == spec.Name && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(spec.Parameters));
            Assert.Equal(spec.ReturnType, method.ReturnType.FullName); Assert.Equal(spec.Static, method.IsStatic); Assert.True(method.HasBody);
        }
        var wormhole = assembly.MainModule.GetType(WormholePairBindings.Wormhole);
        Assert.True(wormhole.Methods.Single(m => m.Name == "get_canUseWormhole").HasBody);
        Assert.True(wormhole.Methods.Single(m => m.Name == "GetConnectedWormholes").HasBody);
    }
}
