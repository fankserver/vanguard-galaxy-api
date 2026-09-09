using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace VGModAPI.Tests;

public sealed class PackageValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vgmodapi-package-" + Guid.NewGuid().ToString("N"));

    public PackageValidationTests() => Directory.CreateDirectory(_root);

    [Fact]
    [Trait("Category", "BinaryInspection")]
    public void StableContractHasOnlyFrameworkReferences() => PackageChecks.ValidateContract(typeof(ILifecycleService).Assembly.Location);

    [Fact]
    [Trait("Category", "BinaryInspection")]
    public void CoreRemainsLoaderAndUnityFree() => PackageChecks.ValidateAssembly(typeof(Core.LifecycleHub).Assembly.Location, "VGModAPI.Core");

    [Fact]
    public void IncorrectIdentityIsRejected() => Assert.Throws<InvalidOperationException>(
        () => PackageChecks.ValidateAssembly(typeof(ILifecycleService).Assembly.Location, "VGModAPI.Core"));

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
    [InlineData("VGModAPI.Unity", "Assembly-CSharp")]
    [InlineData("VGModAPI.Unity", "VGModAPI.Core")]
    [InlineData("VGModAPI.Unity", "BepInEx")]
    [InlineData("VGModAPI.Abstractions", "VGModAPI.Unity")]
    [InlineData("VGModAPI.Core", "VGModAPI.Unity")]
    public void ForbiddenAssemblyReferencesAreRejected(string owner, string dependency)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(typeof(ILifecycleService).Assembly.Location);
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
    public void CompletedApisHaveNoConfigurationSwitches()
    {
        var root = Environment.GetEnvironmentVariable("VG_PACKAGE_ROOT")
            ?? throw new InvalidOperationException("Run make package or set VG_PACKAGE_ROOT for built-package checks.");
        using var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(root, "VGModAPI.dll"));
        var bindings = new HashSet<(string Section, string Key)>();
        foreach (var type in assembly.MainModule.GetTypes())
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            List<string>? arguments = null;
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is MethodReference getter && getter.Name == "get_Config" &&
                    getter.DeclaringType.FullName == "BepInEx.BaseUnityPlugin")
                    arguments = new List<string>();
                if (arguments != null && instruction.OpCode == OpCodes.Ldstr)
                    arguments.Add((string)instruction.Operand);
                if (instruction.Operand is not MethodReference call || call.Name != "Bind" ||
                    call.DeclaringType.FullName != "BepInEx.Configuration.ConfigFile") continue;
                Assert.NotNull(arguments);
                Assert.True(arguments!.Count >= 2, "Configuration bindings must expose their section and key.");
                bindings.Add((arguments[0], arguments[1]));
                arguments = null;
            }
        }
        Assert.Contains(("Persistence", "Root"), bindings);
        foreach (var setting in new[] { ("Persistence", "Enabled"), ("Missions", "Enabled"),
            ("Missions", "IdentityContinuity"), ("Travel", "Enabled"), ("ModInformation", "MenuEnabled"),
            ("Boarding", "Enabled"), ("Dungeons", "Enabled"), ("GameplayUi", "Enabled") })
            Assert.DoesNotContain(setting, bindings);
    }

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
