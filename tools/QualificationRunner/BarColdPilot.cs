using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
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
            Save("qa-owned-bars", LifecycleEventKind.SaveSucceeded); // Same canonical identity, retaining unknown-provider state.
            Require(SpGet(a, "_provider") == null && SpGet(b, "_provider") == null, "Absence phase acquired an author.");
        }
        else
        {
            var pa = (IBarProvider)SpGet(a, "Provider")!; var pb = (IBarProvider)SpGet(b, "Provider")!;
            Require(owned.Length == 2 && owned.Select(member => member.OwnedId!.Value).ToHashSet().SetEquals(new[] {
                new BarPatronId(pa.ProviderId, "contact"), new BarPatronId(pb.ProviderId, "contact") }), "Cold saved identities did not reconstruct without Place.");
            Require(owned.Select(member => member.Seed).ToHashSet().SetEquals(new[] { "owned-bar-a", "owned-bar-b" }), "Cold saved seeds changed.");
        }
        WriteAtomic("bar-cold-" + phase + ".txt", new[] { "PASS", absent ? "fresh-process;unregistered-providers;no-owned-presentation;same-save" : "fresh-process;retained-identities;retained-seeds;no-place-after-absence" });
        Passed("owned-bar-cold-" + phase);
    }
}
