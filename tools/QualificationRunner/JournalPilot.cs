using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private const string JournalSuffix = ".vgmissionjournal.json";
    private HashSet<string> _previousJournalIds = new();
    private bool _journalDisposed;
    private bool JournalCoordinated => File.Exists(Path.Combine(_root!, "journal-coordinated.enabled"));
    private readonly Dictionary<string, string[]> _coordinatedJournalSnapshots = new(StringComparer.Ordinal);
    private int _coordinatedJournalRoundtrips;
    private bool JournalSelected => File.Exists(Path.Combine(_root!, "missionjournal.enabled"));

    private static object? JournalFacade() => Assembly.Load("VGMissionJournal")
        .GetType("VGMissionJournal.Api.MissionJournalApi", true)!.GetProperty("Current")!.GetValue(null);

    private static string[] JournalIds(IEnumerable records) => records.Cast<object>()
        .Select(r => (string)r.GetType().GetProperty("MissionInstanceId")!.GetValue(r)!).OrderBy(s => s, StringComparer.Ordinal).ToArray();

    private static string[] StoredJournalIds(string path)
    {
        var schema = Assembly.Load("VGMissionJournal").GetType("VGMissionJournal.Persistence.JournalSchema", true)!;
        var json = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true)!;
        var settings = schema.GetProperty("SerializerSettings")!.GetValue(null)!;
        var value = json.GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type), settings.GetType() })!
            .Invoke(null, new[] { File.ReadAllText(path), (object)schema, settings })!;
        return JournalIds((IEnumerable)schema.GetProperty("Missions")!.GetValue(value)!);
    }

    private static string[] LiveJournalIds()
    {
        var facade = JournalFacade(); Require(facade != null, "Journal facade missing.");
        return JournalIds((IEnumerable)facade!.GetType().GetMethod("GetAllMissions")!.Invoke(facade, null)!);
    }

    private void CheckJournalLoad(string name)
    {
        if (!JournalSelected) return;
        var expected = JournalCoordinated && _coordinatedJournalSnapshots.TryGetValue(name, out var captured)
            ? captured : StoredJournalIds(Path.Combine(_saveRoot!, name + ".save" + JournalSuffix));
        if (JournalCoordinated && _coordinatedJournalSnapshots.ContainsKey(name))
        {
            Require(!File.Exists(Path.Combine(_saveRoot!, name + ".save" + JournalSuffix)), "Coordinated roundtrip has a legacy fallback file.");
            _coordinatedJournalRoundtrips++;
        }
        Require(expected.Length > 0, "Journal pilot requires nonempty copied history.");
        var actual = new HashSet<string>(LiveJournalIds());
        Require(expected.All(actual.Contains), "Copied journal history was not restored.");
        Require(!_previousJournalIds.Except(expected).Any(actual.Contains), "Journal history leaked between slots.");
        _previousJournalIds = new HashSet<string>(expected);
        Passed("journal-history-" + name);
    }

    private void CheckJournalSave(string name, LifecycleEventKind outcome)
    {
        if (!JournalSelected) return;
        var path = Path.Combine(_saveRoot!, name + ".save" + JournalSuffix);
        if (_journalDisposed || outcome != LifecycleEventKind.SaveSucceeded)
        {
            Require(!File.Exists(path), "Journal wrote for a failed/skipped save or after teardown.");
            return;
        }
        if (JournalCoordinated)
        {
            Require(!File.Exists(path), "Coordinated journal unexpectedly wrote a legacy sidecar.");
            var controller = SpGet(Chainloader.PluginInfos["vgmissionjournal"].Instance, "_lifecycle")!;
            if (StockpileCoordinated && (_coordinatedStorageBlocked || _stockpileDisposed))
            {
                Require(!(bool)SpGet(controller, "CanRecord")!, "Coordinated journal did not pause with shared storage/refused provider.");
                return;
            }
            Require(controller.GetType().Name == "CoordinatedPersistence" && (bool)SpGet(controller, "CanRecord")!, "Coordinated journal was not ready after save.");
            _coordinatedJournalSnapshots[name] = LiveJournalIds();
            return;
        }
        Require(File.Exists(path), "Successful save has no journal sidecar: " + name);
        Require(StoredJournalIds(path).SequenceEqual(LiveJournalIds()), "Journal saved the wrong history/destination.");
    }

    private IEnumerable<object?> CheckJournalTeardown()
    {
        if (!JournalSelected) yield break;
        Require(Chainloader.PluginInfos.TryGetValue("vgmissionjournal", out var info) && info.Instance != null, "Journal plugin missing before teardown.");
        UnityEngine.Object.Destroy(info!.Instance);
        foreach (var frame in Wait(() => JournalFacade() == null, "journal teardown")) yield return frame;
        _journalDisposed = true;
        Save("qa-journal-disposed", LifecycleEventKind.SaveSucceeded);
        if (JournalCoordinated)
        {
            Require(_coordinatedJournalRoundtrips > 0 && _coordinatedJournalSnapshots.Count > 0, "No actual journal coordinated roundtrip observed.");
            File.WriteAllText(Path.Combine(_root!, "journal-coordinated.txt"), "PASS\nActual MissionJournal history-ID roundtrip without legacy output sidecars; copied legacy import explicit.");
            Passed("actual-journal-coordinated-roundtrip-and-teardown");
        }
        Passed("journal-teardown");
    }
}
