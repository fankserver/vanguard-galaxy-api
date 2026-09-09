using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckOwnedWorld()
    {
        Require(File.Exists(Path.Combine(_root!, "world.enabled")), "World phase is not armed.");
        var authors = RequireWorldAuthors();
        WriteAtomic("owned-world.txt", new[] { "INCOMPLETE" });
        foreach (var frame in LoadReady("fixture-a")) yield return frame;
        foreach (var frame in Wait(NativeTravelReady, "world fixture native readiness")) yield return frame;
        var session = _api!.CurrentSession!.Id;
        var current = SpGet(CurrentPlayer, "currentPointOfInterest") ?? throw new InvalidOperationException("No fixture POI.");
        var system = SpGet(current, "system") ?? throw new InvalidOperationException("No fixture system.");
        var systemId = (string)SpGet(system, "guid")!;
        var firstId = Guid.Parse("681a3868-4420-4a76-bb83-e7155a036017");
        var secondId = Guid.Parse("335308ee-24dc-4c52-8c2d-3d46a0b523ac");
        var before = ((IEnumerable)SpGet(system, "pointsOfInterest")!).Cast<object>().ToArray();
        bool cold = Environment.GetCommandLineArgs().Contains("--vgmodapi-world-cold");
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
        foreach (var id in new[] { first.PoiId!, second.PoiId! })
        {
            var matches = after.Where(poi => (string)SpGet(poi, "guid")! == id).ToArray();
            Require(matches.Length == 1, "Owned identity does not have exactly one native system member.");
            var poi = matches[0];
            Require(poi.GetType() == NativeType("Source.Galaxy.POI.Combat") && ReferenceEquals(SpGet(poi, "system"), system),
                "Owned native type/parent mismatch.");
            foreach (var field in new[] { "units", "persistables", "payloads" })
                Require(!((IEnumerable)SpGet(poi, field)!).Cast<object>().Any(), "Unexpected owned native content: " + field);
        }
        if (!cold) Save("qa-owned-world", LifecycleEventKind.SaveSucceeded);
        WriteAtomic("owned-world.txt", new[] { cold ? "COLD-LOOKUP-NATIVE-MEMBERSHIP" : "PUBLIC-CREATE-NATIVE-MEMBERSHIP-SAVE", first.PoiId!, second.PoiId!,
            "Phase evidence only; paired commit verification, ordered references and control matrix remain separate requirements." });
    }
}
