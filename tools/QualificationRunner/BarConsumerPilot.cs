using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckBarConsumers()
    {
        Require(File.Exists(Path.Combine(_root!, "bar-consumers.enabled")), "Consumer bar phase not armed.");
        WriteAtomic("bar-consumers.txt", new[] { "INCOMPLETE" });
        foreach (var frame in LoadReady("fixture-a")) yield return frame;
        foreach (var frame in Wait(NativeTravelReady, "consumer bar readiness")) yield return frame;
        var anima = Chainloader.PluginInfos["vganima"].Instance;
        var custom = Chainloader.PluginInfos["com.vanguardgalaxy.custommission"].Instance;
        var tts = Chainloader.PluginInfos["vgtts"].Instance;
        Require(anima.enabled && (bool)SpGet(anima, "_active")! && (bool)SpGet(anima, "ManagedBarsSelected")!, "Anima managed provider inactive.");
        Require(SpGet(anima, "LlmClient") == null, "Consumer fixture must not contact an LLM endpoint.");
        var origin = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        var originSystem = SpGet(CurrentPlayer, "currentSystem")!;
        // Exercise the real lazy campaign builder with a controlled source, without accepting a
        // campaign mission or claiming natural travel/progression to the resulting station.
        var mission = Activator.CreateInstance(NativeType("Source.MissionSystem.Mission"))!;
        AccessTools.Field(mission.GetType(), "sourcePoi").SetValue(mission, origin);
        var sector = custom.GetType().Assembly.GetType("VanguardGalaxy.CustomMission.Act3Sector", true)!;
        object? Static(Type type, string method, params object?[] values) => AccessTools.Method(type, method).Invoke(null, values);
        Static(sector, "EnsureBuilt", mission);
        var patrons = custom.GetType().Assembly.GetType("VanguardGalaxy.CustomMission.BarPatrons", true)!;
        Static(patrons, "EnsureFoundationBar");
        var managed = custom.GetType().Assembly.GetType("VanguardGalaxy.CustomMission.ManagedBarPatrons", true)!;
        Static(managed, "Tick");
        var foundation = Static(patrons, "ResolveFoundationStation");
        Require(foundation != null && SpGet(foundation, "bar") != null, "Actual Foundation builder did not create its bar.");
        var stationId = (string)SpGet(foundation!, "guid")!;
        Require(stationId == "CustomAct3RickoStation", "Unexpected Foundation identity.");
        var apiPlugin = Chainloader.PluginInfos[ModApi.PluginId].Instance;
        var permissions = SpGet(apiPlugin, "_barPermissionConfig")!;
        var originalPermissions = SpGet(permissions, "BoxedValue");
        BarRosterFinalized? latest = null;
        using var observer = ModApi.Bars!.Subscribe(Id, value => latest = value);
        var session = _api!.CurrentSession!.Id;
        var bar = SpGet(foundation!, "bar")!;
        var customProvider = (IBarProvider)SpGet(managed, "_provider")!;
        void CheckTtsBoundary()
        {
            var bridgeType = tts.GetType().Assembly.GetType("VGTTS.Patches.BarRosterBridge", true)!;
            var bridge = SpGet(bridgeType, "Current");
            Require(bridge != null, "TTS did not subscribe to finalized rosters.");
            var admission = SpGet(bridge!, "_admission")!;
            var actual = ((IEnumerable)SpGet(bar, "availablePatrons")!).Cast<object>().ToArray();
            var observed = ((IEnumerable)SpGet(admission, "_roster")!).Cast<object>().ToArray();
            Require(actual.Length == observed.Length && actual.Zip(observed, ReferenceEquals).All(match => match), "TTS retained an intermediate roster.");
            var vanilla = ((IEnumerable)SpGet(admission, "_vanilla")!).Cast<object>().ToArray();
            var expected = actual.Where((_, index) => !latest!.Members[index].OwnedId.HasValue).ToArray();
            Require(vanilla.Length == expected.Length && vanilla.Zip(expected, ReferenceEquals).All(match => match), "TTS misclassified owned narrative contacts as vanilla.");
        }
        try
        {
            // No frame advances and no save occurs inside this scoped context. Restore both fields
            // even on failure. This is a bar-composition fixture, not a fabricated travel event.
            AccessTools.Field(CurrentPlayer.GetType(), "currentPointOfInterest").SetValue(CurrentPlayer, foundation);
            AccessTools.Field(CurrentPlayer.GetType(), "currentSystem").SetValue(CurrentPlayer, SpGet(foundation!, "system"));
            SpCall(bar, "CheckUpdatePatrons", false);
            Require(latest?.SessionId == session && latest.StationId == stationId
                && latest.Members.Count == 4 && latest.Members.All(member => member.OwnedId?.Provider == customProvider.ProviderId), "Foundation exclusive roster was not exactly four owned contacts.");
            CheckTtsBoundary();
            var assembly = anima.GetType().Assembly;
            object Make(string name, params object[] values) => Activator.CreateInstance(assembly.GetType("VGAnima.Llm." + name, true)!, values)!;
            Array One(string name, object value) { var array = Array.CreateInstance(assembly.GetType("VGAnima.Llm." + name, true)!, 1); array.SetValue(value, 0); return array; }
            var block = Make("LlmMissionBlock", "Controlled bar offer", "Gather one ore", "Done",
                SpGet(SpGet(foundation!, "faction")!, "identifier")!, One("LlmMissionStep", Make("LlmMissionStep", Make("GatherOreIntent", 1, "Gather one ore"))),
                One("LlmReward", Make("LlmCreditsReward", 1)));
            var story = Make("LlmStory", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), block);
            const string seed = "qa-anima-foundation";
            var contact = Activator.CreateInstance(NativeType("Source.Galaxy.POI.Station.Patrons.Salesman"), seed, foundation)!;
            SpCall(contact, "Initialize");
            var patch = assembly.GetType("VGAnima.Patches.BarRefreshPatches", true)!;
            Static(patch, "FinalizeBrokerInjection", anima, bar, foundation!, contact, seed, story, null);
            var entry = SpCall(SpGet(anima, "PersistedRegistry")!, "FindBySeed", seed);
            Require(entry != null, "Real Anima finalization did not retain its narrative offer.");
            latest = null; SpCall(bar, "CheckUpdatePatrons", false);
            Require(latest != null && latest.Members.Count == 4 && latest.Members.All(member => member.OwnedId?.Provider == customProvider.ProviderId)
                && latest.DeniedProviders.Count > 0, "Anima displaced Foundation or lacked denial diagnostics.");
            CheckTtsBoundary();
            permissions.GetType().GetProperty("BoxedValue")!.SetValue(permissions, "");
            latest = null; SpCall(bar, "CheckUpdatePatrons", false);
            Require(latest != null && latest.DeniedProviders.ContainsKey(customProvider.ProviderId)
                && latest.Members.All(member => member.OwnedId?.Provider != customProvider.ProviderId), "Revoked Foundation permission remained effective.");
            CheckTtsBoundary();
        }
        finally
        {
            permissions.GetType().GetProperty("BoxedValue")!.SetValue(permissions, originalPermissions);
            AccessTools.Field(CurrentPlayer.GetType(), "currentPointOfInterest").SetValue(CurrentPlayer, origin);
            AccessTools.Field(CurrentPlayer.GetType(), "currentSystem").SetValue(CurrentPlayer, originSystem);
        }
        Require(ReferenceEquals(SpGet(CurrentPlayer, "currentPointOfInterest"), origin) && ReferenceEquals(SpGet(CurrentPlayer, "currentSystem"), originSystem), "Controlled context was not restored.");
        WriteAtomic("bar-consumers.txt", new[] { "PASS", "actual-foundation-builder;four-exclusive-contacts;actual-anima-finalization;denied-additive-offer;tts-finalized-boundary;permission-revocation;context-restored" });
        Passed("controlled-bar-consumer-composition");
    }
}
