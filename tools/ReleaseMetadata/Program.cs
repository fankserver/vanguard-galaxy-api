using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using VGModAPI.Core;

namespace VGModAPI.ReleaseMetadata;

internal static class Program
{
    // Read metadata only: never load or execute the candidate plugin or resolve game assemblies.
    internal static string Generate(string assemblyPath, string id, string version, string channel, string releaseUrl)
    {
        if (!Regex.IsMatch(id, @"\A[A-Za-z0-9._-]{1,128}\z") || !ModMetadataCodec.TryVersion(version, out var expected) ||
            channel is not ("stable" or "experimental") || !ModMetadataCodec.IsPublicHttpsUrl(releaseUrl))
            throw new FormatException("Invalid release identity, numeric version, channel or HTTPS URL.");
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        var plugins = assembly.MainModule.GetTypes().SelectMany(type => type.CustomAttributes)
            .Where(attribute => attribute.AttributeType.FullName == "BepInEx.BepInPlugin" && attribute.ConstructorArguments.Count == 3 &&
                attribute.ConstructorArguments[0].Value is string guid && guid == id).ToArray();
        if (plugins.Length != 1 || plugins[0].ConstructorArguments[2].Value is not string metadataVersion ||
            !ModMetadataCodec.TryVersion(metadataVersion, out var actual) || actual != expected || assembly.Name.Version != expected)
            throw new InvalidOperationException("Packaged BepInPlugin GUID/version, assembly version and release version must agree.");
        var json = "{\"schemaVersion\":1,\"pluginId\":\"" + id + "\",\"channel\":\"" + channel +
            "\",\"version\":\"" + expected + "\",\"releaseUrl\":\"" + releaseUrl.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}\n";
        ModUpdateFeed.Parse(Encoding.UTF8.GetBytes(json), id, channel);
        return json;
    }
    private static int Main(string[] args)
    {
        if (args.Length != 6)
        {
            Console.Error.WriteLine("Usage: ReleaseMetadata <packaged.dll> <plugin-guid> <numeric-version> <stable|experimental> <release-url> <output.json>");
            return 2;
        }
        try { File.WriteAllText(args[5], Generate(args[0], args[1], args[2], args[3], args[4]), new UTF8Encoding(false)); return 0; }
        catch (Exception error) { Console.Error.WriteLine("Release metadata rejected: " + error.GetType().Name); return 1; }
    }
}
