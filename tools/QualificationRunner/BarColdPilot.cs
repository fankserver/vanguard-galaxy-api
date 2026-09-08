using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx.Bootstrap;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    // Read-only disk validation through the store's integrity decoder, plus an independently hashed
    // native file. This neither captures an owner nor publishes/mutates a generation.
    private string[] ReadBarGeneration()
    {
        var persistence = SpGet(Chainloader.PluginInfos[ModApi.PluginId].Instance, "_persistence")!;
        var coordinator = SpGet(persistence, "_coordinator")!;
        var path = Path.Combine(_saveRoot!, "qa-owned-bars.save");
        var slot = ((Func<string, string>)SpGet(coordinator, "_canonical")!)(path);
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        var generation = SpCall(SpGet(coordinator, "_store")!, "Load", slot, hash);
        Require(generation != null, "Native save has no committed API generation.");
        var identity = SpGet(generation!, "Identity")!;
        Require((string)SpGet(identity, "Slot")! == slot && (string)SpGet(identity, "VanillaHash")! == hash, "Committed generation identity mismatch.");
        return new[] { "PASS", slot, hash, (string)SpGet(identity, "StateHash")!, SpGet(identity, "Snapshot")!.ToString()! };
    }

    private IEnumerable<object?> CheckColdBars(bool absent)
    {
        string phase = absent ? "absent" : "consumer";
        Require(File.Exists(Path.Combine(_root!, "bar-cold-" + phase + "-started.txt")), "Unplanned cold bar phase.");
        var donor = File.ReadAllLines(Path.Combine(_root!, "bar-cold-donor.txt"));
        Require(donor.Length == 3 && donor[0] == "PASS" && Guid.TryParse(donor[1], out _), "Invalid cold bar donor.");
        WriteAtomic("bar-cold-" + phase + ".txt", new[] { "INCOMPLETE" });
        var a = Chainloader.PluginInfos["vg-bar-author-a"].Instance;
        var b = Chainloader.PluginInfos["vg-bar-author-b"].Instance;
        Require(SpGet(a, "_provider") == null && SpGet(b, "_provider") == null, "Cold phase reused provider runtime state.");
        foreach (var frame in LoadReady("qa-owned-bars")) yield return frame;
        foreach (var frame in Wait(NativeTravelReady, "cold bar native readiness")) yield return frame;
        var generationBefore = ReadBarGeneration();
        var expectedGeneration = File.ReadAllLines(Path.Combine(_root!, absent ? "bar-producer-generation.txt" : "bar-absent-generation.txt"));
        Require(generationBefore.SequenceEqual(expectedGeneration), "Cold process did not receive the finalized predecessor generation.");
        var coordinator = SpGet(SpGet(Chainloader.PluginInfos[ModApi.PluginId].Instance, "_persistence")!, "_coordinator")!;
        Require((string)SpGet(coordinator, "_loadPath")! == generationBefore[1] && (string)SpGet(coordinator, "_loadHash")! == generationBefore[2],
            "API restored a different native generation.");
        var session = _api!.CurrentSession!.Id;
        Require(session != Guid.Parse(donor[1]), "Cold phase reused producer session.");
        var station = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        Require((string)SpGet(station, "guid")! == donor[2], "Cold station identity changed.");
        var api = ModApi.Bars ?? throw new InvalidOperationException("Cold bar API unavailable.");
        BarRosterFinalized? latest = null;
        using var subscription = api.Subscribe(Id, value => latest = value);
        var bar = SpGet(station, "bar")!;
        if (!absent)
        {
            Require(((BarResult)SpCall(a, "Register", donor[2], "contact", "owned-bar-a")).Succeeded, "Cold A registration refused.");
            Require(((BarResult)SpCall(b, "Register", donor[2], "contact", "owned-bar-b")).Succeeded, "Cold B registration refused.");
        }
        SpCall(bar, "CheckUpdatePatrons", false);
        Require(latest != null && latest.SessionId == session && latest.StationId == donor[2], "Missing cold finalized roster.");
        var actual = ((IEnumerable)SpGet(bar, "availablePatrons")!).Cast<object>().ToArray();
        Require(actual.Length == latest!.Members.Count && actual.Select((patron, index) =>
            (string)SpGet(patron, "seed")! == latest.Members[index].Seed && (int)SpGet(patron, "seat")! == latest.Members[index].Seat).All(match => match),
            "Cold finalized observation differs from native presentation.");
        var owned = latest.Members.Where(member => member.OwnedId.HasValue).ToArray();
        if (absent)
        {
            Require(owned.Length == 0, "Unregistered providers produced native contacts.");
            foreach (var frame in Settle()) yield return frame;
            Save("qa-owned-bars", LifecycleEventKind.SaveSucceeded); // Same canonical identity, retaining unknown-provider state.
            var committed = ReadBarGeneration();
            Require(committed[1] == generationBefore[1] && committed[2] != generationBefore[2] && committed[4] != generationBefore[4],
                "Absent phase did not commit a fresh native/API generation at the same identity.");
            WriteAtomic("bar-absent-generation.txt", committed);
            Require(SpGet(a, "_provider") == null && SpGet(b, "_provider") == null, "Absence phase acquired an author.");
        }
        else
        {
            var pa = (IBarProvider)SpGet(a, "Provider")!; var pb = (IBarProvider)SpGet(b, "Provider")!;
            Require(owned.Length == 2 && owned.Select(member => member.OwnedId!.Value).ToHashSet().SetEquals(new[] {
                new BarPatronId(pa.ProviderId, "contact"), new BarPatronId(pb.ProviderId, "contact") }), "Cold saved identities did not reconstruct without Place.");
            Require(owned.Select(member => member.Seed).ToHashSet().SetEquals(new[] { "owned-bar-a", "owned-bar-b" }), "Cold saved seeds changed.");
        }
        WriteAtomic("bar-cold-" + phase + ".txt", new[] { "PASS", absent ? "fresh-process;unregistered-providers;no-owned-presentation;same-save;fresh-commit" : "fresh-process;retained-identities;retained-seeds;no-place-after-absence;bound-generation" });
        Passed("owned-bar-cold-" + phase);
    }
}
