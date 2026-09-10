using System;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledAuthoredSystemBindingTests
{
    [Fact]
    public void AuthoredSystemMembersBindToInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        foreach (var spec in AuthoredSystemBindings.Members)
        {
            var type = assembly.MainModule.GetType(spec.Type);
            Assert.NotNull(type);
            if (spec.Field)
            {
                var field = Assert.Single(type.Fields, value => value.Name == spec.Member);
                Assert.Equal(spec.Shape, field.FieldType.FullName); Assert.Equal(spec.Static, field.IsStatic);
                // Most bound fields are public; protected (JumpGate.jumpgateOpen) and private
                // (SectorMapData.systems) members are read via Public|NonPublic reflection.
                Assert.True(field.IsPublic || field.IsFamily || spec.Member == "systems");
            }
            else
            {
                var property = Assert.Single(type.Properties, value => value.Name == spec.Member);
                Assert.Equal(spec.Shape, property.PropertyType.FullName);
                Assert.Equal(spec.Static, property.GetMethod.IsStatic); Assert.Empty(property.Parameters);
            }
        }
        foreach (var spec in AuthoredSystemBindings.Methods)
        {
            var type = assembly.MainModule.GetType(spec.Type);
            Assert.NotNull(type);
            var method = Assert.Single(type.Methods, value => value.Name == spec.Name &&
                value.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(spec.Parameters));
            Assert.Equal(spec.ReturnType, method.ReturnType.FullName);
            Assert.Equal(spec.Static, method.IsStatic);
        }
        var create = assembly.MainModule.GetType(AuthoredSystemBindings.Sandbox).Methods.Single(m => m.Name == "AddSideContentSystemToSystem");
        Assert.True(create.IsStatic && create.HasBody);
        var entrance = assembly.MainModule.GetType(AuthoredSystemBindings.System).Methods.Single(m => m.Name == "GetEntranceJumpgate");
        Assert.True(entrance.HasBody);
        var gate = assembly.MainModule.GetType(AuthoredSystemBindings.JumpGate);
        Assert.True(gate.Methods.Single(m => m.Name == "UnlockJumpgate").HasBody);
        Assert.True(gate.Methods.Single(m => m.Name == "LockGate").HasBody);
        Assert.True(gate.Methods.Single(m => m.Name == "GetTargetPOI").HasBody);
        Assert.Contains(gate.Fields, field => field.Name == "jumpgateOpen");
        Assert.Contains(assembly.MainModule.GetType("Source.Galaxy.MapPointOfInterest").Fields, field => field.Name == "hidden");
        // Dissolution boundaries: plain membership mutations with no side effects beyond the removal.
        var removePoi = assembly.MainModule.GetType(AuthoredSystemBindings.System).Methods.Single(m => m.Name == "RemovePointOfInterest");
        Assert.True(removePoi.HasBody && !removePoi.IsStatic);
    }
}
