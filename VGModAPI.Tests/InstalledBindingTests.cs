using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledBindingTests
{
    private static string AssemblyPath => Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
        ?? throw new InvalidOperationException("Run make check-bindings or set VG_GAME_ASSEMBLY to the original installed Assembly-CSharp.dll.");

    [Fact]
    public void NativeTravelLocationAndReadinessMembersHaveInspectedShapes()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        void Field(string owner, string name, string type, bool isStatic = false)
        {
            var field = Assert.Single(module.GetType(owner).Fields, candidate => candidate.Name == name);
            Assert.Equal(type, field.FieldType.FullName); Assert.Equal(isStatic, field.IsStatic);
        }
        void Property(string owner, string name, string type)
        {
            var property = Assert.Single(module.GetType(owner).Properties, candidate => candidate.Name == name);
            Assert.Equal(type, property.PropertyType.FullName); Assert.NotNull(property.GetMethod); Assert.False(property.GetMethod.IsStatic);
            Assert.Empty(property.Parameters);
        }
        const string player = "Source.Player.GamePlayer";
        const string element = "Source.Galaxy.MapElement";
        const string poi = "Source.Galaxy.MapPointOfInterest";
        const string system = "Source.Galaxy.SystemMapData";
        const string manager = "Behaviour.Managers.BasePoiManager";
        const string travel = "Behaviour.Managers.TravelManager";
        Field(player, "current", player, true); Field(player, "currentSystem", system); Field(player, "currentPointOfInterest", poi);
        Field(element, "system", system); Field(element, "_name", "System.String");
        Property(element, "guid", "System.String"); Property(player, "elapsedTime", "System.Double");
        Property(travel, "localPoiManager", manager); Property(travel, "localTarget", poi); Property(travel, "targetPoi", poi);
        Property(manager, "poi", poi); Property(manager, "initializedAndReady", "System.Boolean");
    }

    [Fact]
    public void NativePoiArrivalHierarchyHasConcreteBaseAndTrueOverrideDeclarations()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var types = assembly.MainModule.Types.ToDictionary(type => type.FullName);
        const string rootName = "Behaviour.Managers.BasePoiManager";
        var root = types[rootName];
        var baseArrival = Assert.Single(root.Methods, method => method.Name == "SpaceshipHasArrived" && method.Parameters.Count == 0);
        Assert.True(root.IsAbstract); Assert.True(baseArrival.IsVirtual); Assert.True(baseArrival.HasBody);
        bool Derived(TypeDefinition type)
        {
            for (TypeDefinition? cursor = type; cursor != null; cursor = cursor.BaseType != null && types.TryGetValue(cursor.BaseType.FullName, out var parent) ? parent : null)
                if (cursor.FullName == rootName) return true;
            return false;
        }
        var declarations = types.Values.Where(Derived).SelectMany(type => type.Methods)
            .Where(method => method.Name == "SpaceshipHasArrived" && method.Parameters.Count == 0).ToArray();
        Assert.True(declarations.Length > 1);
        foreach (var method in declarations)
        {
            Assert.True(method.IsVirtual); Assert.False(method.IsAbstract); Assert.True(method.HasBody);
            Assert.Equal("System.Void", method.ReturnType.FullName);
            if (method != baseArrival) Assert.False(method.IsNewSlot);
        }
    }

    [Fact]
    public void WaveProbeBindingsMatchNativeGenerationAndLaunch()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        foreach (var kind in new[] { "Industry", "Patrol" })
        {
            var data = assembly.MainModule.GetType("Source.Galaxy.POI.Station." + kind + "Board");
            Assert.Single(data.Methods, m => m.Name == "Generate" + kind + "Missions" && m.Parameters.Count == 0);
            var ui = assembly.MainModule.GetType("Behaviour.UI.Spacestation.Location." + kind + "Board");
            Assert.Single(ui.Methods, m => m.Name == "LaunchClicked" && m.Parameters.Count == 0);
            var mission = assembly.MainModule.GetType("Source.MissionSystem." + kind + "Mission");
            Assert.Single(mission.Methods, m => m.Name == "ClaimRewards" && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "System.Boolean" }));
            Assert.Contains(mission.Fields, f => f.Name == "wave" && f.FieldType.FullName == "System.Int32");
        }
        Assert.Contains(assembly.MainModule.GetType("Behaviour.UI.Missions.FocusedMissionHandler").Properties, p => p.Name == "focusedMission");
        var travel = assembly.MainModule.GetType("Behaviour.Managers.TravelManager");
        Assert.Contains(travel.Properties, p => p.Name == "targetPoi");
        Assert.Single(travel.Methods, m => m.Name == "IsLocalPoiReady" && m.Parameters.Count == 0 && m.ReturnType.FullName == "System.Boolean");
    }

    [Fact]
    public void AnimaProbeResolvesPlayerStoryLookupInsteadOfBareOverloadedName()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var methods = assembly.MainModule.GetType("Source.MissionSystem.StoryMission").Methods.Where(m => m.Name == "Get").ToArray();
        Assert.True(methods.Length > 1, "Fixture no longer exercises overloaded lookup.");
        var target = Assert.Single(methods, m => m.IsStatic && m.Parameters.Select(p => p.ParameterType.FullName)
            .SequenceEqual(new[] { "Source.Player.GamePlayer", "System.String" }));
        Assert.Equal("Source.MissionSystem.Mission", target.ReturnType.FullName);
        Assert.True(target.HasBody && target.Body.Instructions.Count > 2);
    }

    [Fact]
    public void AssemblyMatchesInspectedAdapterIdentity()
    {
        using var file = File.OpenRead(AssemblyPath);
        Assert.Equal(BindingCatalog.InspectedSha256, Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant());
    }

    [Fact]
    public void EveryPatchHasAnExactNonStubMethodBody()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        foreach (var binding in BindingCatalog.Session.Concat(BindingCatalog.Saves).Concat(BindingCatalog.Missions).Concat(BindingCatalog.MissionSnapshots))
        {
            var type = assembly.MainModule.GetType(binding.Type);
            Assert.True(type != null, "Missing type: " + binding.Type);
            var matches = type!.Methods.Where(m => m.Name == binding.Name && m.IsStatic == binding.Static
                && m.ReturnType.FullName == binding.ReturnType
                && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(binding.Parameters)).ToArray();
            Assert.True(matches.Length == 1, "Binding mismatch: " + binding.Key + " / " + binding.Type + "." + binding.Name);
            Assert.True(matches[0].HasBody && matches[0].Body.Instructions.Count > 2, "Missing/non-original body: " + binding.Key);
        }
    }

    [Fact]
    public void GuildProbeSignaturesAndConstructorsMatchInstalledAssembly()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var ammo = Assert.Single(assembly.MainModule.GetType("Behaviour.Unit.AbstractUnit").Methods, m => m.Name == "AmmoInCargoForTurrets");
        Assert.Equal("System.Boolean", ammo.ReturnType.FullName);
        var parameter = Assert.Single(ammo.Parameters);
        Assert.Equal("System.Boolean", parameter.ParameterType.FullName); Assert.True(parameter.HasConstant); Assert.Equal(false, parameter.Constant);
        foreach (var kind in new[] { "Bounty", "Patrol", "Industry" })
        {
            var mission = assembly.MainModule.GetType("Source.MissionSystem." + kind + "Mission");
            Assert.Contains(mission.Methods, m => m.IsConstructor && !m.IsStatic && m.IsPublic && m.Parameters.Count == 0);
            var board = assembly.MainModule.GetType("Behaviour.UI.Spacestation.Location." + kind + "Board");
            var selected = Assert.Single(board.Fields, f => f.Name == "selectedMission");
            Assert.Equal(mission.FullName, selected.FieldType.FullName);
            var data = assembly.MainModule.GetType("Source.Galaxy.POI.Station." + kind + "Board");
            Assert.Contains(data.Methods, m => m.IsConstructor && m.IsPublic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "Source.Galaxy.POI.SpaceStation");
            var station = assembly.MainModule.GetType("Source.Galaxy.POI.SpaceStation");
            Assert.Equal(data.FullName, Assert.Single(station.Fields, f => f.Name == char.ToLowerInvariant(kind[0]) + kind.Substring(1) + "Board").FieldType.FullName);
        }
    }

    [Theory]
    [InlineData(BindingCatalog.Player, "current", BindingCatalog.Player, true)]
    [InlineData(BindingCatalog.Player, "isEphemeral", "System.Boolean", false)]
    [InlineData(BindingCatalog.File, "File", "System.IO.FileInfo", false)]
    [InlineData(BindingCatalog.Save, "SavesPath", "System.String", true)]
    [InlineData("GameplayManager", "_initialized", "System.Boolean", false)]
    [InlineData(BindingCatalog.Player, "missions", "System.Collections.Generic.List`1<Source.MissionSystem.Mission>", false)]
    [InlineData(BindingCatalog.Player, "missionsArchive", "System.Collections.Generic.List`1<System.String>", false)]
    [InlineData(BindingCatalog.Player, "currentBounty", "Source.MissionSystem.BountyMission", false)]
    [InlineData(BindingCatalog.Player, "currentPatrol", "Source.MissionSystem.PatrolMission", false)]
    [InlineData(BindingCatalog.Player, "currentIndustry", "Source.MissionSystem.IndustryMission", false)]
    [InlineData(BindingCatalog.Mission, "name", "System.String", false)]
    [InlineData(BindingCatalog.Mission, "storyId", "System.String", false)]
    [InlineData(BindingCatalog.Mission, "failed", "System.Boolean", false)]
    [InlineData("Source.MissionSystem.Objectives.Mining", "itemCategory", "System.Nullable`1<Source.Item.ItemCategory>", false)]
    public void AdapterFieldsMatchInstalledAssembly(string owner, string name, string fieldType, bool isStatic)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var field = Assert.Single(assembly.MainModule.GetType(owner).Fields, f => f.Name == name);
        Assert.Equal(fieldType, field.FieldType.FullName); Assert.Equal(isStatic, field.IsStatic);
    }
}
