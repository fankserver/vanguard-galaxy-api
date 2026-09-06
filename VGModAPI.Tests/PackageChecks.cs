using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace VGModAPI.Tests;

internal static class PackageChecks
{
    internal static readonly string[] Assemblies = { "VGModAPI", "VGModAPI.Core", "VGModAPI.Abstractions" };
    internal static readonly string[] Documents =
    {
        "checks.md", "compatibility.md", "implementation-plan.md", "lifecycle-contract.md",
        "qualification-runner.md", "research-findings.md", "persistence-identity.md", "persistence-schema.md"
    };
    internal static readonly string[] Files = Assemblies.Select(n => n + ".dll")
        .Concat(new[] { "README.md", "LICENSE" }).Concat(Documents.Select(n => "docs/" + n)).ToArray();

    internal static void ValidateLayout(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(root)) throw new InvalidOperationException("Package directory is missing.");
        var actual = new HashSet<string>(StringComparer.Ordinal);
        Inspect(root, "", actual);
        if (!actual.SetEquals(Files))
            throw new InvalidOperationException("Package allowlist mismatch. Missing: " + string.Join(", ", Files.Except(actual))
                + "; unexpected: " + string.Join(", ", actual.Except(Files)));
    }

    private static void Inspect(string directory, string prefix, HashSet<string> files)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Package directories must not be links.");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var relative = prefix + Path.GetFileName(path);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Package entries must not be links: " + relative);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (relative != "docs") throw new InvalidOperationException("Unexpected package directory: " + relative);
                Inspect(path, relative + "/", files);
            }
            else files.Add(relative);
        }
    }

    internal static void ValidateContract(string path) => ValidateAssembly(path, "VGModAPI.Abstractions");

    internal static void ValidateAssembly(string path, string expectedName)
    {
        string[] allowed = expectedName switch
        {
            "VGModAPI.Abstractions" => new[] { "netstandard" },
            "VGModAPI.Core" => new[] { "netstandard", "VGModAPI.Abstractions" },
            "VGModAPI" => new[] { "netstandard", "VGModAPI.Abstractions", "VGModAPI.Core", "BepInEx", "0Harmony", "UnityEngine", "UnityEngine.CoreModule" },
            _ => throw new InvalidOperationException("Unknown owned assembly: " + expectedName)
        };
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        if (assembly.Name.Name != expectedName) throw new InvalidOperationException("Wrong assembly identity: " + expectedName);
        var forbidden = assembly.MainModule.AssemblyReferences.Where(r => !allowed.Contains(r.Name)).Select(r => r.Name).ToArray();
        if (forbidden.Length != 0) throw new InvalidOperationException("Unexpected " + expectedName + " dependencies: " + string.Join(", ", forbidden));
    }
}
