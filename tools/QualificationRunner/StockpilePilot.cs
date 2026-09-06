using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using VGModAPI;
using UnityEngine;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private Assembly? _stockpileAssembly;
    private static bool _pauseStockpileDriver;
    private bool StockpileSelected => File.Exists(Path.Combine(_root!, "stockpile.enabled"));
    private string StockpilePath(string name) => Path.Combine(_saveRoot!, name + ".vgstockpile-transfers.json");
    private BaseUnityPlugin Stockpile => Chainloader.PluginInfos["vgstockpile"].Instance;
    private object StockpileEngine => SpGet(Stockpile, "_engine") ?? throw new InvalidOperationException("Stockpile engine missing.");

    private void ArmStockpile(Harmony harmony)
    {
        if (!StockpileSelected) return;
        _stockpileAssembly = Assembly.Load("VGStockpile");
        _pauseStockpileDriver = true;
        harmony.Patch(AccessTools.Method(_stockpileAssembly.GetType("VGStockpile.Transfers.Engine.TransferEngineDriver", true), "Update"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(AllowStockpileDriver)));
    }
    private static bool AllowStockpileDriver() => !_pauseStockpileDriver;
    private static object? SpGet(object target, string name)
    {
        var type = target as Type ?? target.GetType();
        var instance = target is Type ? null : target;
        var field = AccessTools.Field(type, name);
        if (field != null) return field.GetValue(instance);
        return (AccessTools.Property(type, name) ?? throw new MissingMemberException(type.FullName, name)).GetValue(instance);
    }
    private static object SpCall(object target, string method, params object[] args)
        => AccessTools.Method(target.GetType(), method).Invoke(target, args)!;
    private static string SpJson(object value) => (string)Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true)!
        .GetMethod("SerializeObject", new[] { typeof(object) })!.Invoke(null, new[] { value })!;
    private object SpRead(string path) => Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true)!
        .GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type) })!.Invoke(null,
            new object[] { File.ReadAllText(path), _stockpileAssembly!.GetType("VGStockpile.Transfers.Persistence.TransferSidecar", true)! })!;
    private void CheckStockpileLoad(string name)
    {
        if (!StockpileSelected) return;
        Require(Stockpile.enabled, "Stockpile disabled in full pilot.");
        var snapshot = SpCall(StockpileEngine, "Snapshot");
        Require(File.Exists(StockpilePath(name)) ? SpJson(snapshot) == SpJson(SpRead(StockpilePath(name)))
            : !((IEnumerable)SpGet(snapshot, "Items")!).Cast<object>().Any(), "Stockpile copied queue restore mismatch.");
    }
    private object[] SpStations() => ((IEnumerable)SpGet(SpGet(AccessTools.TypeByName("Source.Galaxy.GalaxyMapData"), "current")!, "allPointsOfInterest")!)
        .Cast<object>().Where(p => AccessTools.TypeByName("Source.Galaxy.POI.SpaceStation").IsInstanceOfType(p) && SpGet(p, "materialStorage") != null).ToArray();
    private int SpQuantity(string guid, string item)
    {
        var station = SpStations().Single(p => (string)SpGet(p, "guid")! == guid);
        return ((IEnumerable)SpGet(SpGet(station, "materialStorage")!, "items")!).Cast<object>()
            .Where(slot => SpGet(slot, "item") is { } type && (string)SpGet(type, "identifier")! == item)
            .Sum(slot => (int)SpGet(slot, "count")!);
    }
    private long SpCredits => (long)SpGet(SpGet(_player, "current")!, "credits")!;

    private IEnumerable<object?> CheckStockpilePilot()
    {
        if (!StockpileSelected) yield break;
        foreach (var frame in Wait(() => (bool)SpGet(Stockpile, "IconAttached")!, "Stockpile HUD hook")) yield return frame;
        CheckStockpileLoad("fixture-a");
        // Explicit fixture setup: drop only copied in-memory jobs, without refunding/mutating inventory.
        // Driver remains paused while imported state is inspected and controlled jobs are exercised.
        var empty = _stockpileAssembly!.GetType("VGStockpile.Transfers.Persistence.TransferSidecar", true)!.GetMethod("Empty")!.Invoke(null, null)!;
        SpCall(StockpileEngine, "Restore", empty);
        var stations = SpStations();
        var source = stations.First(p => ((IEnumerable)SpGet(SpGet(p, "materialStorage")!, "items")!).Cast<object>().Any(s => (int)SpGet(s, "count")! >= 1));
        var slot = ((IEnumerable)SpGet(SpGet(source, "materialStorage")!, "items")!).Cast<object>().First(s => (int)SpGet(s, "count")! >= 1);
        var item = (string)SpGet(SpGet(slot, "item")!, "identifier")!;
        var sourceId = (string)SpGet(source, "guid")!;
        var destId = (string)SpGet(stations.First(s => (string)SpGet(s, "guid")! != sourceId), "guid")!;
        var lineType = _stockpileAssembly.GetType("VGStockpile.Transfers.TransferManifestLine", true)!;
        var manifest = Array.CreateInstance(lineType, 1);
        manifest.SetValue(Activator.CreateInstance(lineType, item, 1), 0);
        var sourceCount = SpQuantity(sourceId, item); var destCount = SpQuantity(destId, item); var credits = SpCredits;
        var result = SpCall(StockpileEngine, "RequestTransfer", sourceId, destId, manifest, 0);
        Require((bool)SpGet(result, "IsSuccess")!, "Controlled transfer request refused.");
        var request = SpGet(result, "Created")!;
        var fee = (int)SpGet(request, "FeeCredits")!;
        Require(SpQuantity(sourceId, item) == sourceCount - 1 && SpCredits == credits - fee, "Reservation/debit mismatch.");
        Require((bool)SpCall(StockpileEngine, "CancelTransfer", SpGet(request, "Id")!), "Cancellation refused.");
        Require(SpQuantity(sourceId, item) == sourceCount && SpCredits == credits - fee, "Cancellation changed refund/fee semantics.");
        result = SpCall(StockpileEngine, "RequestTransfer", sourceId, destId, manifest, 0);
        Require((bool)SpGet(result, "IsSuccess")!, "Second controlled transfer request refused.");
        request = SpGet(result, "Created")!;
        var requestId = (string)SpGet(request, "Id")!;
        var creditsAfter = SpCredits;
        Save("qa-stockpile-pending", LifecycleEventKind.SaveSucceeded);
        Require(SpJson(SpRead(StockpilePath("qa-stockpile-pending"))) == SpJson(SpCall(StockpileEngine, "Snapshot")), "Saved transfer snapshot mismatch.");
        Load("qa-stockpile-pending");
        foreach (var frame in Wait(() => _api!.CurrentSession?.Phase == SessionPhase.GameplayInitialized, "Stockpile reload")) yield return frame;
        foreach (var frame in Settle()) yield return frame;
        CheckStockpileLoad("qa-stockpile-pending");
        CheckJournalLoad("qa-stockpile-pending");
        Require(SpCredits == creditsAfter && SpQuantity(sourceId, item) == sourceCount - 1, "Reload duplicated reservation/debit.");
        var beforeFailure = SpJson(SpCall(StockpileEngine, "Snapshot"));
        var blocked = Path.Combine(_saveRoot!, "qa-stockpile-failure.meta");
        Directory.CreateDirectory(blocked);
        try { Save("qa-stockpile-failure", LifecycleEventKind.SaveFailed); }
        finally { Directory.Delete(blocked); }
        Require(!File.Exists(StockpilePath("qa-stockpile-failure")) && SpJson(SpCall(StockpileEngine, "Snapshot")) == beforeFailure, "Failed save changed transfer persistence or queue.");
        var protectedPath = StockpilePath("qa-stockpile-protected");
        const string future = "{\"Version\":99,\"Items\":[]}";
        File.WriteAllText(protectedPath, future);
        Save("qa-stockpile-protected", LifecycleEventKind.SaveSucceeded);
        Require(File.ReadAllText(protectedPath) == future && !((IEnumerable)SpCall(StockpileEngine, "Tick", float.MaxValue)).Cast<object>().Any(), "Protected write did not pause mutation.");
        result = SpCall(StockpileEngine, "RequestTransfer", sourceId, destId, manifest, 0);
        Require(SpGet(result, "Error")!.ToString() == "PersistenceUnavailable", "Write pause reason missing.");
        Save("qa-stockpile-retry", LifecycleEventKind.SaveSucceeded);
        Require(File.Exists(StockpilePath("qa-stockpile-retry")), "Retry snapshot missing.");
        // Bring only the controlled queue near completion, then exercise the actual Unity driver.
        SpCall(StockpileEngine, "Tick", Math.Max(0f, (float)SpGet(request, "RemainingSeconds")! - 0.01f));
        _pauseStockpileDriver = false;
        foreach (var frame in Wait(() => !((IEnumerable)SpGet(StockpileEngine, "Pending")!).Cast<object>().Any(r => (string)SpGet(r, "Id")! == requestId), "Stockpile driver delivery")) yield return frame;
        _pauseStockpileDriver = true;
        Require(SpQuantity(destId, item) == destCount + 1 && SpQuantity(sourceId, item) == sourceCount - 1 && SpCredits == creditsAfter, "Delivery inventory/credits mismatch.");
        Passed("Stockpile real reservation/cancellation/fees, save/reload, protected-write retry, and driver delivery");
        Load("qa-stockpile-protected");
        foreach (var frame in Wait(() => _api!.CurrentSession?.Phase == SessionPhase.GameplayInitialized, "Stockpile protected restore")) yield return frame;
        result = SpCall(StockpileEngine, "RequestTransfer", sourceId, destId, manifest, 0);
        Require(SpGet(result, "Error")!.ToString() == "PersistenceUnavailable" && File.ReadAllText(protectedPath) == future, "Protected restore did not disable transfers.");
        File.WriteAllText(protectedPath, "{ corrupt");
        Load("qa-stockpile-protected");
        foreach (var frame in Wait(() => _api!.CurrentSession?.Phase == SessionPhase.GameplayInitialized, "Stockpile corrupt restore")) yield return frame;
        result = SpCall(StockpileEngine, "RequestTransfer", sourceId, destId, manifest, 0);
        Require(SpGet(result, "Error")!.ToString() == "PersistenceUnavailable" && File.ReadAllText(protectedPath) == "{ corrupt", "Corrupt restore did not disable transfers.");
        Load("fixture-b");
        foreach (var frame in Wait(() => _api!.CurrentSession?.Phase == SessionPhase.GameplayInitialized, "Stockpile slot replacement")) yield return frame;
        CheckStockpileLoad("fixture-b"); CheckJournalLoad("fixture-b");
        Require(!((IEnumerable)SpGet(StockpileEngine, "Pending")!).Cast<object>().Any(r => (string)SpGet(r, "Id")! == requestId), "Old job leaked into replacement slot.");
        var engine = StockpileEngine;
        var owned = new[] { "_driver", "_icon", "_window", "_refineryIcon", "_refineryWindow" }
            .Select(name => SpGet(Stockpile, name) as UnityEngine.Object).ToArray();
        UnityEngine.Object.Destroy(Stockpile);
        yield return null; yield return null;
        Require(owned.All(value => !value), "Stockpile driver/UI survived teardown.");
        Require(!Harmony.GetAllPatchedMethods().Any(m => Harmony.GetPatchInfo(m)?.Owners.Contains("vgstockpile") == true), "Stockpile patches survived teardown.");
        Require(!((IEnumerable)SpCall(engine, "Tick", float.MaxValue)).Cast<object>().Any(), "Disposed engine mutated.");
        Save("qa-stockpile-disposed", LifecycleEventKind.SaveSucceeded);
        Require(!File.Exists(StockpilePath("qa-stockpile-disposed")), "Disposed Stockpile persisted.");
        Passed("Stockpile protected restore, slot replacement, and teardown; journal coexistence retained");
    }
}
