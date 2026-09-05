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
        foreach (var binding in BindingCatalog.Session.Concat(BindingCatalog.Saves))
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

    [Theory]
    [InlineData(BindingCatalog.Player, "current", BindingCatalog.Player, true)]
    [InlineData(BindingCatalog.Player, "isEphemeral", "System.Boolean", false)]
    [InlineData(BindingCatalog.File, "File", "System.IO.FileInfo", false)]
    [InlineData(BindingCatalog.Save, "SavesPath", "System.String", true)]
    [InlineData("GameplayManager", "_initialized", "System.Boolean", false)]
    public void AdapterFieldsMatchInstalledAssembly(string owner, string name, string fieldType, bool isStatic)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var field = Assert.Single(assembly.MainModule.GetType(owner).Fields, f => f.Name == name);
        Assert.Equal(fieldType, field.FieldType.FullName); Assert.Equal(isStatic, field.IsStatic);
    }
}
