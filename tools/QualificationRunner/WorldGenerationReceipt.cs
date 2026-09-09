using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx.Bootstrap;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    // Read-only decoder access is evidence about disk metadata, not public creation or publication.
    private string[] ReadWorldGeneration(string first, string second)
    {
        var persistence = SpGet(Chainloader.PluginInfos[ModApi.PluginId].Instance, "_persistence")!;
        var coordinator = SpGet(persistence, "_coordinator")!;
        var path = Path.Combine(_saveRoot!, "qa-owned-world.save");
        var slot = ((Func<string, string>)SpGet(coordinator, "_canonical")!)(path);
        Require(new FileInfo(path).Length <= 64 * 1024 * 1024, "World native save exceeds probe limit.");
        var bytes = File.ReadAllBytes(path);
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        var reader = SpCall(persistence, "CreateWorldReader");
        var generation = SpCall(reader, "Read", slot, bytes);
        var association = SpGet(generation, "Association")!;
        Require((string)SpGet(association, "Slot")! == slot && (string)SpGet(association, "VanillaHash")! == hash,
            "World generation does not match current native bytes and canonical slot.");
        var rows = ((IEnumerable)SpGet(generation, "Rows")!).Cast<object>().ToArray();
        Require(rows.Length == 2, "Unexpected persisted world instance count.");
        var ids = rows.Select(row => (string)SpGet(SpGet(row, "Identity")!, "NativeId")!).ToArray();
        Require(ids.Distinct(StringComparer.Ordinal).Count() == 2 && ids.Contains(first) && ids.Contains(second),
            "Committed world identities differ from native/public identities.");
        foreach (var row in rows)
        {
            var identity = SpGet(row, "Identity")!;
            bool authorA = (string)SpGet(identity, "NativeId")! == first;
            var owner = authorA ? "vgmodapi.qualification.world.a" : "vgmodapi.qualification.world.b";
            var instance = Guid.Parse(authorA ? "681a3868-4420-4a76-bb83-e7155a036017" : "335308ee-24dc-4c52-8c2d-3d46a0b523ac");
            Require((string)SpGet(identity, "Owner")! == owner && (string)SpGet(identity, "LocalId")! == "PoiX" &&
                (Guid)SpGet(identity, "InstanceId")! == instance, "Persisted author/instance tuple mismatch.");
            var retained = SpCall(generation, "DefinitionFor", row) ?? throw new InvalidOperationException("Missing paired retained declaration.");
            var definition = SpGet(retained, "Definition")!;
            Require((string)SpGet(retained, "Owner")! == owner && (string)SpGet(definition, "LocalId")! == "PoiX" &&
                (int)SpGet(definition, "Revision")! == 1 && (int)SpGet(row, "DefinitionRevision")! == 1 &&
                (string)SpGet(definition, "Name")! == "Empty qualification site" &&
                (string)SpGet(definition, "FactionId")! == "player" && (int)SpGet(definition, "Level")! == 1,
                "Retained declaration differs from the expected author definition.");
        }
        Require(SpCall(generation, "PayloadFor", "vgmodapi.world-definitions") is byte[], "World definitions payload missing.");
        return new[] { "PAIRED-WORLD-GENERATION", slot, hash, (string)SpGet(association, "StateHash")!,
            SpGet(association, "Snapshot")!.ToString()!, first, second };
    }
}
