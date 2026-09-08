using System;
using System.Linq;
using System.IO;
using Mono.Cecil;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledWorldBindingTests
{
    [Fact]
    public void NativeAssetRegistriesAndUnityLifetimeMatchInspection()
    {
        var path = Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings against the original installed assembly.");
        using var game = AssemblyDefinition.ReadAssembly(path);
        foreach (var entry in new[] { (Type: "Behaviour.Unit.SpaceShip", Field: "allShips"), (Type: "Behaviour.Equipment.Builder.EquipmentBuilder", Field: "allBuilders") })
        {
            var field = game.MainModule.GetType(entry.Type).Fields.Single(candidate => candidate.Name == entry.Field);
            Assert.True(field.IsStatic);
            Assert.Equal("System.Collections.Generic.Dictionary`2<System.String," + entry.Type + ">", field.FieldType.FullName);
        }
        using var unity = AssemblyDefinition.ReadAssembly(Path.Combine(Path.GetDirectoryName(path)!, "UnityEngine.CoreModule.dll"));
        var type = unity.MainModule.GetType("UnityEngine.Object");
        var pointer = type.Fields.Single(field => field.Name == "m_CachedPtr");
        Assert.False(pointer.IsStatic); Assert.Equal("System.IntPtr", pointer.FieldType.FullName);
        var getPointer = type.Methods.Single(method => method.Name == "GetCachedPtr");
        Assert.Contains(getPointer.Body.Instructions, instruction => instruction.Operand is FieldReference field && field.Name == "m_CachedPtr");
        var alive = type.Methods.Single(method => method.Name == "IsNativeObjectAlive");
        Assert.Contains(alive.Body.Instructions, instruction => instruction.Operand is MethodReference method && method.Name == "GetCachedPtr");
    }

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
        var element = module.GetType("Source.Galaxy.MapElement");
        foreach (string fieldName in new[] { "<guid>k__BackingField", "_name", "system", "position", "level", "<faction>k__BackingField" })
            Assert.False(element.Fields.Single(field => field.Name == fieldName).IsStatic);
        Assert.True(module.GetType("Source.Galaxy.Faction").Fields.Single(field => field.Name == "allFactions").IsStatic);
        foreach (string seed in new[] { "backgroundSeed", "contentSeed" })
            Assert.Equal("System.UInt64", module.GetType("Source.Galaxy.MapPointOfInterest").Fields.Single(field => field.Name == seed).FieldType.FullName);
        foreach (string typeName in new[] { "Source.Galaxy.MapElement", "Source.Galaxy.MapPointOfInterest", "Source.Galaxy.POI.Combat" })
        {
            var constructor = module.GetType(typeName).Methods.Single(method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 0);
            Assert.DoesNotContain(constructor.Body.Instructions, instruction => instruction.Operand is MethodReference method &&
                (method.DeclaringType.FullName.Contains("SeededRandom") || method.Name == "SetupPOI" || method.DeclaringType.FullName == "Source.Galaxy.Faction"));
        }
        var poiRead = module.GetType("Source.Galaxy.MapPointOfInterest").Methods.Single(method => method.Name == "FromJson");
        var calls = poiRead.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (MethodReference)instruction.Operand).ToArray();
        int create = Array.FindIndex(calls, method => method.Name == "Create" && method.DeclaringType.FullName == "Source.Galaxy.MapPointOfInterest");
        int load = Array.FindIndex(calls, method => method.Name == "LoadFromJson");
        Assert.True(create >= 0 && load > create, "Validation must precede the native type factory, not just property loading.");
        var staged = module.GetType("Source.Util.SaveGame").NestedTypes.Single(type => type.Name.StartsWith("<LoadStateStaged>", StringComparison.Ordinal));
        var stagedCalls = staged.Methods.Single(method => method.Name == "MoveNext").Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference).Select(instruction => (MethodReference)instruction.Operand).ToArray();
        int futureCheck = Array.FindIndex(stagedCalls, method => method.DeclaringType.FullName == "Source.Util.GameVersion" && method.Name == "IsFuture");
        int playerFactory = Array.FindIndex(stagedCalls, method => method.DeclaringType.FullName == "Source.Player.GamePlayer" && method.Name == "FromJsonStaged");
        Assert.True(futureCheck >= 0 && playerFactory > futureCheck, "The API-required envelope relies on native future-version refusal preceding player/world construction.");
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
