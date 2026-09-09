using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using Mono.Cecil;

namespace VGModAPI.Tests;

internal static class PackageChecks
{
    internal static readonly string[] Assemblies = { "VGModAPI", "VGModAPI.Core", "VGModAPI.Abstractions", "VGModAPI.Unity" };
    internal static void ValidatePluginVersion(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var plugin = assembly.MainModule.GetType("VGModAPI.Plugin") ?? throw new InvalidOperationException("Plugin type missing.");
        var attributes = plugin.CustomAttributes.Where(a => a.AttributeType.FullName == "BepInEx.BepInPlugin").ToArray();
        if (attributes.Length != 1 || attributes[0].ConstructorArguments.Count != 3 ||
            attributes[0].ConstructorArguments[2].Value is not string text || !Version.TryParse(text, out var version))
            throw new InvalidOperationException("Plugin version metadata missing or malformed.");
        var normalized = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        if (normalized != assembly.Name.Version) throw new InvalidOperationException("Plugin metadata and assembly version disagree.");
    }

    internal static void ValidateContract(string path) => ValidateAssembly(path, "VGModAPI.Abstractions");

    internal static void ValidateAssembly(string path, string expectedName)
    {
        string[] allowed = expectedName switch
        {
            "VGModAPI.Abstractions" => new[] { "netstandard" },
            "VGModAPI.Core" => new[] { "netstandard", "VGModAPI.Abstractions" },
            "VGModAPI.Unity" => new[] { "netstandard", "VGModAPI.Abstractions", "UnityEngine.CoreModule" },
            "VGModAPI" => new[] { "netstandard", "VGModAPI.Abstractions", "VGModAPI.Core", "VGModAPI.Unity", "BepInEx", "0Harmony", "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.UIModule", "UnityEngine.UI", "Unity.TextMeshPro", "Unity.InputSystem" },
            _ => throw new InvalidOperationException("Unknown owned assembly: " + expectedName)
        };
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        if (assembly.Name.Name != expectedName) throw new InvalidOperationException("Wrong assembly identity: " + expectedName);
        var forbidden = assembly.MainModule.AssemblyReferences.Where(r => !allowed.Contains(r.Name)).Select(r => r.Name).ToArray();
        if (forbidden.Length != 0) throw new InvalidOperationException("Unexpected " + expectedName + " dependencies: " + string.Join(", ", forbidden));
    }
}
