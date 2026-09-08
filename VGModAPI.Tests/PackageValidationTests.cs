using System;
using System.IO;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

public sealed class PackageValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vgmodapi-package-" + Guid.NewGuid().ToString("N"));

    public PackageValidationTests() => Directory.CreateDirectory(_root);

    [Fact]
    [Trait("Category", "BinaryInspection")]
    public void StableContractHasOnlyFrameworkReferences() => PackageChecks.ValidateContract(typeof(ILifecycleApi).Assembly.Location);

    [Fact]
    [Trait("Category", "BinaryInspection")]
    public void CoreRemainsLoaderAndUnityFree() => PackageChecks.ValidateAssembly(typeof(Core.LifecycleHub).Assembly.Location, "VGModAPI.Core");

    [Fact]
    public void IncorrectIdentityIsRejected() => Assert.Throws<InvalidOperationException>(
        () => PackageChecks.ValidateAssembly(typeof(ILifecycleApi).Assembly.Location, "VGModAPI.Core"));

    [Theory]
    [InlineData("VGModAPI.Abstractions", "UnityEngine")]
    [InlineData("VGModAPI.Abstractions", "VGModAPI.Core")]
    [InlineData("VGModAPI.Core", "UnityEngine")]
    [InlineData("VGModAPI.Core", "BepInEx")]
    [InlineData("VGModAPI.Abstractions", "UnityEngine.UI")]
    [InlineData("VGModAPI.Abstractions", "Unity.TextMeshPro")]
    [InlineData("VGModAPI.Abstractions", "Unity.InputSystem")]
    [InlineData("VGModAPI.Core", "UnityEngine.UI")]
    [InlineData("VGModAPI.Core", "Unity.TextMeshPro")]
    [InlineData("VGModAPI.Core", "Unity.InputSystem")]
    [InlineData("VGModAPI", "Assembly-CSharp")]
    public void ForbiddenAssemblyReferencesAreRejected(string owner, string dependency)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(typeof(ILifecycleApi).Assembly.Location);
        assembly.Name.Name = owner;
        assembly.MainModule.AssemblyReferences.Add(new AssemblyNameReference(dependency, new Version(1, 0)));
        var altered = Path.Combine(_root, "altered.dll");
        assembly.Write(altered);
        Assert.Throws<InvalidOperationException>(() => PackageChecks.ValidateAssembly(altered, owner));
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.2", false)]
    public void PluginVersionMustMatchAssembly(string version, bool valid)
    {
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("VGModAPI", new Version(1, 2, 3, 0)), "VGModAPI", ModuleKind.Dll);
        var module = assembly.MainModule;
        var plugin = new TypeDefinition("VGModAPI", "Plugin", TypeAttributes.Public, module.TypeSystem.Object);
        module.Types.Add(plugin);
        var attributeType = new TypeReference("BepInEx", "BepInPlugin", module, new AssemblyNameReference("BepInEx", new Version(5, 0)));
        var ctor = new MethodReference(".ctor", module.TypeSystem.Void, attributeType) { HasThis = true };
        for (int i = 0; i < 3; i++) ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var attribute = new CustomAttribute(ctor);
        foreach (var value in new[] { "vgmodapi", "API", version }) attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, value));
        plugin.CustomAttributes.Add(attribute);
        var file = Path.Combine(_root, "plugin.dll"); assembly.Write(file);
        if (valid)
        {
            PackageChecks.ValidatePluginVersion(file);
            File.WriteAllText(Path.Combine(_root, "vgmodapi.vgmod.json"), "{\"schemaVersion\":1,\"pluginId\":\"vgmodapi\",\"channel\":\"stable\",\"updateUrl\":\"https://github.com/example/mod/releases/latest/download/update.json\"}");
            var feed = ReleaseMetadata.Program.Generate(file, "vgmodapi", "1.2.3", "stable", "https://github.com/example/mod/releases/tag/v1.2.3");
            Assert.Equal(new Version(1, 2, 3, 0), Core.ModUpdateFeed.Parse(System.Text.Encoding.UTF8.GetBytes(feed), "vgmodapi", "stable").Version);
            Assert.Throws<InvalidOperationException>(() => ReleaseMetadata.Program.Generate(file, "wrong.guid", "1.2.3", "stable", "https://github.com/example/mod/releases/tag/v1.2.3"));
            Assert.Throws<FormatException>(() => ReleaseMetadata.Program.Generate(file, "vgmodapi", "1.2.3-beta", "stable", "https://github.com/example/mod/releases/tag/v1.2.3"));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => PackageChecks.ValidatePluginVersion(file));
            Assert.Throws<InvalidOperationException>(() => ReleaseMetadata.Program.Generate(file, "vgmodapi", "1.2.3", "stable", "https://github.com/example/mod/releases/tag/v1.2.3"));
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

[Trait("Category", "Package")]
public sealed class BuiltPackageTests
{
    [Fact]
    public void BuiltPackageAssembliesAndMetadataAreValid()
    {
        var root = Environment.GetEnvironmentVariable("VG_PACKAGE_ROOT")
            ?? throw new InvalidOperationException("Run make package or set VG_PACKAGE_ROOT for built-package checks.");
        PackageChecks.ValidatePluginVersion(Path.Combine(root, "VGModAPI.dll"));
        var metadata = Core.ModMetadataCodec.Parse(File.ReadAllBytes(Path.Combine(root, "vgmodapi.vgmod.json")), ModApi.PluginId);
        Assert.Equal("https://github.com/fankserver/vanguard-galaxy-api", metadata.ProjectUrl);
        Assert.False(string.IsNullOrWhiteSpace(metadata.Description));
        foreach (var name in PackageChecks.Assemblies)
            PackageChecks.ValidateAssembly(Path.Combine(root, name + ".dll"), name);
    }
}
