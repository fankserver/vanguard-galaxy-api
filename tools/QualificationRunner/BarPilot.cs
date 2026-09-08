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
    private IEnumerable<object?> CheckOwnedBars()
    {
        if (!File.Exists(Path.Combine(_root!, "bars.enabled"))) yield break;
        WriteAtomic("owned-bars.txt", new[] { "INCOMPLETE" });
        foreach (var frame in LoadReady("fixture-a")) yield return frame;
        foreach (var frame in Wait(NativeTravelReady, "bar fixture native readiness")) yield return frame;
        var api = ModApi.Bars ?? throw new InvalidOperationException("Owned bars unavailable.");
        var a = Chainloader.PluginInfos["vg-bar-author-a"].Instance;
        var b = Chainloader.PluginInfos["vg-bar-author-b"].Instance;
        Require(a.GetType().Assembly != b.GetType().Assembly, "Bar authors must be independent assemblies.");
        var station = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        Require(NativeType("Source.Galaxy.POI.SpaceStation").IsInstanceOfType(station), "Bar fixture must start at a native station.");
        var stationId = (string)SpGet(station, "guid")!;
        var bar = SpGet(station, "bar")!;
        BarRosterFinalized? latest = null;
        using var observer = api.Subscribe(Id, roster => latest = roster);
        void Refresh(int owned)
        {
            latest = null;
            SpCall(bar, "CheckUpdatePatrons", false);
            Require(latest != null && latest.SessionId == _api!.CurrentSession!.Id && latest.StationId == stationId,
                "Missing current finalized observation from native refresh.");
            Require(latest!.Members.Count(member => member.OwnedId.HasValue) == owned, "Unexpected owned roster membership.");
            Require(latest.Members.Select(member => member.Seat).Distinct().Count() == latest.Members.Count, "Duplicate native seat.");
            var actual = ((IEnumerable)SpGet(bar, "availablePatrons")!).Cast<object>().ToArray();
            Require(actual.Length == latest.Members.Count && actual.Select((patron, index) =>
                (string)SpGet(patron, "seed")! == latest.Members[index].Seed
                && (int)SpGet(patron, "seat")! == latest.Members[index].Seat
                && patron.GetType().Name == latest.Members[index].NativeKind).All(match => match),
                "Finalized observation differs from the actual native roster.");
        }
        Refresh(0);
        // Controlled preparation through the real force-refresh entry point, not roster edits or
        // fabricated free seats. Fail if eight bounded native refreshes do not provide capacity.
        int preparationRefreshes = 0;
        while (((IEnumerable)SpGet(bar, "availablePatrons")!).Cast<object>().Count() > 3 && preparationRefreshes < 8)
        {
            yield return null;
            SpCall(bar, "CheckUpdatePatrons", true);
            preparationRefreshes++;
        }
        Refresh(0);
        var baseline = ((IEnumerable)SpGet(bar, "availablePatrons")!).Cast<object>().ToArray();
        Require(baseline.Length <= 3, "Controlled native preparation did not provide two additive seats.");
        WriteAtomic("owned-bar-preparation.txt", new[] { "native-force-refreshes=" + preparationRefreshes, "retained-vanilla=" + baseline.Length });
        var baselineJson = SpCall(bar, "ToJson");
        Require((bool)SpGet(baselineJson, "IsJsonObject")!, "Baseline native JSON is not an object.");
        var patronsJson = baselineJson.GetType().GetProperty("Item", new[] { typeof(string) })!.GetValue(baselineJson, new object[] { "availablePatrons" })!;
        Require((bool)SpGet(patronsJson, "IsJsonArray")!, "Baseline JSON has no patron array.");
        var array = SpGet(patronsJson, "AsJsonArray")!;
        Require((int)SpGet(array, "Count")! == baseline.Length, "Baseline JSON lost vanilla patrons.");
        for (int index = 0; index < baseline.Length; index++)
        {
            var item = array.GetType().GetProperty("Item", new[] { typeof(int) })!.GetValue(array, new object[] { index })!;
            Require(item.ToString() == SpCall(baseline[index], "ToJson").ToString(), "Baseline JSON changed vanilla patron content.");
        }
        Require((int)SpGet(SpGet(baselineJson, "AsJsonObject")!, "Count")! == 3, "Unexpected native bar JSON shape.");
        foreach (var field in new[] { "lastUpdateTime", "nextUpdateSeed" })
        {
            var value = baselineJson.GetType().GetProperty("Item", new[] { typeof(string) })!.GetValue(baselineJson, new object[] { field })!;
            var expected = SpGet(bar, field)?.ToString();
            Require(expected == null ? (bool)SpGet(value, "IsNull")! :
                (bool)SpGet(value, "IsString")! && (string)SpGet(value, "AsString")! == expected,
                "Baseline JSON changed native metadata: " + field);
        }
        var baselineText = baselineJson.ToString();
        const string local = "contact";
        Require(((BarResult)SpCall(a, "Register", stationId, local, "owned-bar-a")).Succeeded, "Author A registration refused.");
        Require(((BarResult)SpCall(b, "Register", stationId, local, "owned-bar-b")).Succeeded, "Author B registration refused.");
        var pa = (IBarProvider)SpGet(a, "Provider")!;
        var pb = (IBarProvider)SpGet(b, "Provider")!;
        Require(pa.ProviderId != pb.ProviderId, "Provider IDs collided.");
        var session = _api!.CurrentSession!.Id;
        Require(pa.Place(session, local).Succeeded && pb.Place(session, local).Succeeded, "Persistent placement refused.");
        Refresh(2); Refresh(2);
        Require(latest!.Members.Where(member => member.OwnedId.HasValue).Select(member => member.OwnedId!.Value)
            .ToHashSet().SetEquals(new[] { new BarPatronId(pa.ProviderId, local), new BarPatronId(pb.ProviderId, local) }),
            "Same-local-ID contacts were not independently admitted.");
        var before = SpGet(bar, "availablePatrons");
        var serialized = SpCall(bar, "ToJson").ToString()!;
        Require(ReferenceEquals(before, SpGet(bar, "availablePatrons")), "Serialization replaced the native roster.");
        Require(serialized == baselineText, "Owned roster serialization changed retained vanilla JSON or metadata.");
        Require(!serialized.Contains("owned-bar-a") && !serialized.Contains("owned-bar-b"), "Owned contacts leaked into native JSON.");
        Require(pa.ConfigureStation(stationId, BarRosterOwnership.Exclusive).Succeeded, "Fixture requires explicit A exclusive permission.");
        Refresh(1);
        Require(latest!.DeniedProviders.ContainsKey(pb.ProviderId), "Denied additive author was not diagnosed.");
        Require(pb.ConfigureStation(stationId, BarRosterOwnership.Exclusive).Succeeded, "Fixture requires explicit B exclusive permission.");
        Refresh(0);
        Require(latest!.DeniedProviders.ContainsKey(pa.ProviderId) && latest.DeniedProviders.ContainsKey(pb.ProviderId), "Conflicting exclusive claims not diagnosed.");
        Require(baseline.SequenceEqual(((IEnumerable)SpGet(bar, "availablePatrons")!).Cast<object>()), "Exclusive conflict lost vanilla roster.");
        Require(pa.ConfigureStation(stationId, BarRosterOwnership.Additive).Succeeded && pb.ConfigureStation(stationId, BarRosterOwnership.Additive).Succeeded, "Additive reset refused.");
        Refresh(2);
        Save("qa-owned-bars", LifecycleEventKind.SaveSucceeded);
        foreach (var frame in LoadReady("qa-owned-bars")) yield return frame;
        foreach (var frame in Wait(NativeTravelReady, "owned bar reload readiness")) yield return frame;
        station = SpGet(CurrentPlayer, "currentPointOfInterest")!; bar = SpGet(station, "bar")!;
        Require((string)SpGet(station, "guid")! == stationId, "Reload changed station identity.");
        Refresh(2); // No provider Place or save/load callback after reload.
        Require(pa.Remove(session, local).Status == BarStatus.StaleSession, "Stale session removal was accepted.");
        SpCall(a, "Release"); Refresh(1);
        Require(((BarResult)SpCall(a, "Register", stationId, local, "owned-bar-a")).Succeeded, "Re-registration refused.");
        Refresh(2); // Persistent row survived runtime provider removal.
        WriteAtomic("owned-bars.txt", new[] { "PASS", "independent-authors;repeated-check-update;native-json;exclusive-denial;exclusive-conflict;reload;stale-session;provider-reconstruction" });
        Passed("owned-bar-core-composition");
    }
}
