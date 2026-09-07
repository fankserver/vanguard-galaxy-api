using System;
using System.IO;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledModMenuBindingTests
{
    [Fact]
    public void ModMenuBindingsUseInspectedNativeInstanceStyleFieldsAndModalGetter()
    {
        var path = Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings.");
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var menu = assembly.MainModule.GetType("Behaviour.UI.MainMenuUI");
        Assert.Equal("UnityEngine.MonoBehaviour", menu.BaseType.FullName);
        void Field(string name, string type, bool isStatic, bool isPublic)
        {
            var field = Assert.Single(menu.Fields, candidate => candidate.Name == name);
            Assert.Equal(type, field.FieldType.FullName); Assert.Equal(isStatic, field.IsStatic); Assert.Equal(isPublic, field.IsPublic);
        }
        Field("instance", "Behaviour.UI.MainMenuUI", true, true);
        Field("continueGame", "UnityEngine.UI.Button", false, false);
        Field("versionNumber", "TMPro.TMP_Text", false, false);
        var popup = assembly.MainModule.GetType("Behaviour.UI.AlertPopup");
        var modal = Assert.Single(popup.Properties, property => property.Name == "IsOpen");
        Assert.Equal("System.Boolean", modal.PropertyType.FullName); Assert.Empty(modal.Parameters);
        Assert.True(modal.GetMethod.IsStatic); Assert.True(modal.GetMethod.IsPublic); Assert.True(modal.GetMethod.HasBody);
    }

    [Theory]
    [InlineData("UnityEngine.UI", "UnityEngine.UI.Button")]
    [InlineData("Unity.TextMeshPro", "TMPro.TextMeshProUGUI")]
    [InlineData("Unity.InputSystem", "UnityEngine.InputSystem.UI.InputSystemUIInputModule")]
    [InlineData("UnityEngine.UIModule", "UnityEngine.Canvas")]
    public void MenuCompileReferencesAreActualInstalledUiModules(string module, string type)
    {
        var path = Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings.");
        using var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(Path.GetDirectoryName(path)!, module + ".dll"));
        Assert.NotNull(assembly.MainModule.GetType(type));
    }
}
