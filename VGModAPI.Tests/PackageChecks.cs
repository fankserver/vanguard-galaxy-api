using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using Mono.Cecil;

namespace VGModAPI.Tests;

internal static class PackageChecks
{
    internal static readonly string[] Assemblies = { "VGModAPI", "VGModAPI.Core", "VGModAPI.Abstractions" };
    internal static void ValidateQualificationLayout(string root)
    {
        var expected = new HashSet<string>(Assemblies.Select(name => name + ".dll").Append("README.md"), StringComparer.Ordinal);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked qualification package root.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if ((File.GetAttributes(entry) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || !expected.Remove(Path.GetFileName(entry)))
                throw new InvalidOperationException("Unexpected qualification package entry.");
        }
        if (expected.Count != 0) throw new InvalidOperationException("Incomplete qualification package.");
    }

    internal static void ValidatePluginVersion(string path, bool qualification = false)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var markers = assembly.CustomAttributes.Where(attribute => attribute.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute" &&
            attribute.ConstructorArguments.Count >= 1 && Equals(attribute.ConstructorArguments[0].Value, "VGModAPI.WorldQualification")).ToArray();
        if (!qualification && markers.Length != 0) throw new InvalidOperationException("Qualification-only API cannot enter a normal package.");
        if (qualification && (markers.Length != 1 || markers[0].ConstructorArguments.Count != 2 || !Equals(markers[0].ConstructorArguments[1].Value, "empty-combat-v1")))
            throw new InvalidOperationException("Qualification package marker missing or invalid.");
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
            "VGModAPI" => new[] { "netstandard", "VGModAPI.Abstractions", "VGModAPI.Core", "BepInEx", "0Harmony", "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.UIModule", "UnityEngine.UI", "Unity.TextMeshPro", "Unity.InputSystem" },
            _ => throw new InvalidOperationException("Unknown owned assembly: " + expectedName)
        };
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        if (assembly.Name.Name != expectedName) throw new InvalidOperationException("Wrong assembly identity: " + expectedName);
        var forbidden = assembly.MainModule.AssemblyReferences.Where(r => !allowed.Contains(r.Name)).Select(r => r.Name).ToArray();
        if (forbidden.Length != 0) throw new InvalidOperationException("Unexpected " + expectedName + " dependencies: " + string.Join(", ", forbidden));
    }
}
