using System;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledDroneBayBindingTests
{
    [Fact]
    public void DroneBayTuningMembersBindToInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings."));
        foreach (var spec in DroneBayBindings.Members)
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
        var bay = assembly.MainModule.GetType(DroneBayBindings.Bay);
        foreach (var spec in DroneBayBindings.Methods.Where(binding => binding.Type == DroneBayBindings.Bay))
        {
            var method = Assert.Single(bay.Methods, value => value.Name == spec.Name &&
                value.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(spec.Parameters));
            Assert.Equal(spec.ReturnType, method.ReturnType.FullName);
            Assert.True(method.HasBody, spec.Name + " must have a body to patch or invoke.");
        }
        var catalog = assembly.MainModule.GetType("Behaviour.Unit.Drone").Methods.Single(method =>
            method.Name == "Get" && method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "System.String");
        Assert.True(catalog.IsStatic && catalog.IsPublic && catalog.HasBody);
        Assert.Equal("Behaviour.Unit.Drone", catalog.ReturnType.FullName);
        // AddNewDrone routes through the same initialisation natively created drones use.
        var addDrone = bay.Methods.Single(method => method.Name == "AddNewDrone");
        Assert.Contains(addDrone.Body.Instructions, instruction => instruction.Operand is MethodReference target &&
            target.Name == "CreateAndInitializeDrone");
        // The launch getter is only read inside the deploy coroutine; a prefix cannot break creation.
        Assert.Contains(bay.NestedTypes, nested => nested.Name.Contains("CoroutineDeployDrones", StringComparison.Ordinal));
    }
}
