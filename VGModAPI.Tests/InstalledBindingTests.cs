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
    public void AssemblyMatchesInspectedAdapterIdentity()
    {
        using var file = File.OpenRead(AssemblyPath);
        Assert.Equal(BindingCatalog.InspectedSha256, Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant());
    }

    [Fact]
    public void EveryPatchHasAnExactNonStubMethodBody()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        foreach (var binding in BindingCatalog.Session.Concat(BindingCatalog.Saves).Concat(BindingCatalog.Missions))
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
