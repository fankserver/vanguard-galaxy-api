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
    private bool JournalSelected => File.Exists(Path.Combine(_root!, "missionjournal.enabled"));

    private static object? JournalFacade() => Assembly.Load("VGMissionJournal")
        .GetType("VGMissionJournal.Api.MissionJournalApi", true)!.GetProperty("Current")!.GetValue(null);

    private static string[] JournalIds(IEnumerable records) => records.Cast<object>()
        .Select(r => (string)r.GetType().GetProperty("MissionInstanceId")!.GetValue(r)!).OrderBy(s => s, StringComparer.Ordinal).ToArray();

    private static string[] StoredJournalIds(string path)
    {
        var schema = Assembly.Load("VGMissionJournal").GetType("VGMissionJournal.Persistence.JournalSchema", true)!;
        var json = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true)!;
        var value = json.GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type) })!.Invoke(null, new object[] { File.ReadAllText(path), schema })!;
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
        var expected = StoredJournalIds(Path.Combine(_saveRoot!, name + ".save" + JournalSuffix));
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
        Passed("journal-teardown");
    }
}
