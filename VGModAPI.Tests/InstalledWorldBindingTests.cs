using System;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledWorldBindingTests
{
    [Fact]
    public void ConstructionAndSnapshotBoundariesMatchInspectedAssembly()
    {
        var path = Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings against the original installed assembly.");
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var module = assembly.MainModule;
        foreach (var binding in WorldNativeBindings.Methods)
        {
            var method = module.GetType(binding.Type).Methods.Single(candidate => candidate.Name == binding.Name &&
                candidate.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(binding.Parameters));
            Assert.Equal(binding.Static, method.IsStatic);
            Assert.Equal(binding.ReturnType, method.ReturnType.FullName);
        }
        var poiRead = module.GetType("Source.Galaxy.MapPointOfInterest").Methods.Single(method => method.Name == "FromJson");
        var calls = poiRead.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (MethodReference)instruction.Operand).ToArray();
        int create = Array.FindIndex(calls, method => method.Name == "Create" && method.DeclaringType.FullName == "Source.Galaxy.MapPointOfInterest");
        int load = Array.FindIndex(calls, method => method.Name == "LoadFromJson");
        Assert.True(create >= 0 && load > create, "Validation must precede the native type factory, not just property loading.");
        var snapshot = module.GetType("Source.Util.SaveGame").Methods.Single(method => method.Name == "SaveCurrentState");
        Assert.Contains(snapshot.Body.Instructions, instruction => instruction.Operand is MethodReference method &&
            method.Name == "ToJson" && method.DeclaringType.FullName == "Source.Player.GamePlayer");
        var combatUpdate = module.GetType("Source.Galaxy.POI.Combat").Methods.Single(method => method.Name == "AmbientUpdate");
        var combatCalls = combatUpdate.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (MethodReference)instruction.Operand).ToArray();
        int baseUpdate = Array.FindIndex(combatCalls, method => method.Name == "AmbientUpdate" && method.DeclaringType.FullName == "Source.Galaxy.MapPointOfInterest");
        int remove = Array.FindIndex(combatCalls, method => method.Name == "RemovePointOfInterest");
        Assert.True(baseUpdate >= 0 && remove > baseUpdate, "Removal-only guards do not quarantine the base update.");
        var setup = module.GetType("Source.Galaxy.SystemMapData").Methods.Single(method => method.Name == "SetupPOI");
        Assert.Contains(setup.Body.Instructions, instruction => instruction.Operand is MethodReference method && method.Name == "UpdateLocalPosition");
    }
}
