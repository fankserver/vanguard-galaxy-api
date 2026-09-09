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
            Require(SpCall(generation, "DefinitionFor", row) != null, "Missing paired retained declaration.");
        Require(SpCall(generation, "PayloadFor", "vgmodapi.world-definitions") is byte[], "World definitions payload missing.");
        return new[] { "PAIRED-WORLD-GENERATION", slot, hash, (string)SpGet(association, "StateHash")!,
            SpGet(association, "Snapshot")!.ToString()!, first, second };
    }
}
