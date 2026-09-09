using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private List<(object System, object Poi)> WorldNativeMembers()
    {
        int visited = 0;
        IEnumerable<object> Members(object owner, string field)
        {
            var list = SpGet(owner, field) as IList ?? throw new InvalidOperationException("Missing native membership: " + field);
            Require(list.Count <= 10000, "Excessive native membership collection.");
            for (int index = 0; index < list.Count; index++)
            {
                Require(++visited <= 100000, "Excessive native map traversal.");
                yield return list[index] ?? throw new InvalidOperationException("Null native member.");
            }
        }
        var result = new List<(object System, object Poi)>();
        var map = SpGet(CurrentPlayer, "map") ?? throw new InvalidOperationException("Missing native map.");
        foreach (var sector in Members(map, "sectors"))
            foreach (var system in Members(sector, "systems"))
                foreach (var poi in Members(system, "pointsOfInterest")) result.Add((system, poi));
        return result;
    }

    private IEnumerable<object?> CheckOwnedWorld()
    {
        Require(File.Exists(Path.Combine(_root!, "world.enabled")), "World phase is not armed.");
        var arguments = Environment.GetCommandLineArgs();
        Require(arguments.Count(argument => argument == "--vgmodapi-world-only" || argument == "--vgmodapi-world-cold") == 1,
            "Exactly one world phase must be selected.");
        var authors = RequireWorldAuthors();
        WriteAtomic("owned-world.txt", new[] { "INCOMPLETE" });
        bool cold = arguments.Contains("--vgmodapi-world-cold");
        string[]? createdGeneration = null;
        if (cold)
        {
            var receipt = Path.Combine(_root!, "world-created-generation.txt");
            Require(new FileInfo(receipt).Length <= 4096, "Oversized creation-generation receipt.");
            createdGeneration = File.ReadAllLines(receipt);
            Require(createdGeneration.Length == 7 && createdGeneration[0] == "PAIRED-WORLD-GENERATION", "Missing creation-generation receipt.");
            Require(ReadWorldGeneration(createdGeneration[5], createdGeneration[6]).SequenceEqual(createdGeneration), "Cold input differs from committed creation output.");
        }
        foreach (var frame in LoadReady(cold ? "qa-owned-world" : "fixture-a")) yield return frame;
        foreach (var frame in Wait(NativeTravelReady, "world fixture native readiness")) yield return frame;
        var session = _api!.CurrentSession!.Id;
        var current = SpGet(CurrentPlayer, "currentPointOfInterest") ?? throw new InvalidOperationException("No fixture POI.");
        var system = SpGet(current, "system") ?? throw new InvalidOperationException("No fixture system.");
        var systemId = (string)SpGet(system, "guid")!;
        var firstId = Guid.Parse("681a3868-4420-4a76-bb83-e7155a036017");
        var secondId = Guid.Parse("335308ee-24dc-4c52-8c2d-3d46a0b523ac");
        var before = ((IEnumerable)SpGet(system, "pointsOfInterest")!).Cast<object>().ToArray();
        var first = cold ? (WorldSiteResult)SpCall(authors.A, "Find", session, firstId)
            : (WorldSiteResult)SpCall(authors.A, "Create", session, firstId, systemId, 100f, 100f);
        Require(first.Status == WorldStatus.Succeeded, "World author A creation refused: " + first.Status);
        var second = cold ? (WorldSiteResult)SpCall(authors.B, "Find", session, secondId)
            : (WorldSiteResult)SpCall(authors.B, "Create", session, secondId, systemId, 200f, 100f);
        Require(second.Status == WorldStatus.Succeeded, "World author B creation refused: " + second.Status);
        Require(!string.IsNullOrEmpty(first.PoiId) && !string.IsNullOrEmpty(second.PoiId) && first.PoiId != second.PoiId,
            "Independent world identities collided.");
        var foundA = (WorldSiteResult)SpCall(authors.A, "Find", session, firstId);
        var foundB = (WorldSiteResult)SpCall(authors.B, "Find", session, secondId);
        Require(foundA.Status == WorldStatus.Succeeded && foundA.PoiId == first.PoiId &&
            foundB.Status == WorldStatus.Succeeded && foundB.PoiId == second.PoiId, "Public world lookup mismatch.");
        var after = ((IEnumerable)SpGet(system, "pointsOfInterest")!).Cast<object>().ToArray();
        if (!cold)
        {
            Require(after.Length == before.Length + 2, "Unexpected native membership count after creation.");
            for (int index = 0; index < before.Length; index++)
                Require(ReferenceEquals(before[index], after[index]), "Creation changed pre-existing native membership/order.");
        }
        var global = WorldNativeMembers();
        foreach (var id in new[] { first.PoiId!, second.PoiId! })
        {
            var matches = after.Where(poi => (string)SpGet(poi, "guid")! == id).ToArray();
            Require(matches.Length == 1, "Owned identity does not have exactly one native system member.");
            var poi = matches[0];
            var everywhere = global.Where(member => (string)SpGet(member.Poi, "guid")! == id).ToArray();
            Require(everywhere.Length == 1 && ReferenceEquals(everywhere[0].Poi, poi) && ReferenceEquals(everywhere[0].System, system),
                "Owned native identity is duplicated or misplaced in the galaxy.");
            Require(poi.GetType() == NativeType("Source.Galaxy.POI.Combat") && ReferenceEquals(SpGet(poi, "system"), system),
                "Owned native type/parent mismatch.");
            foreach (var field in new[] { "units", "persistables", "payloads" })
                Require(!((IEnumerable)SpGet(poi, field)!).Cast<object>().Any(), "Unexpected owned native content: " + field);
        }
        if (!cold) Save("qa-owned-world", LifecycleEventKind.SaveSucceeded);
        var committed = ReadWorldGeneration(first.PoiId!, second.PoiId!);
        if (cold) Require(committed.SequenceEqual(createdGeneration!), "Cold load changed the saved generation association.");
        WriteAtomic(cold ? "world-cold-generation.txt" : "world-created-generation.txt", committed);
        WriteAtomic("owned-world.txt", new[] { cold ? "COLD-LOOKUP-NATIVE-MEMBERSHIP" : "PUBLIC-CREATE-NATIVE-MEMBERSHIP-SAVE", first.PoiId!, second.PoiId!,
            "Phase evidence only; paired commit verification, ordered references and control matrix remain separate requirements." });
    }
}
