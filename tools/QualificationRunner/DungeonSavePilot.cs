using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using UnityEngine;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckDungeonSaveLoad()
    {
        WriteAtomic("dungeon-save.txt", new[] { "INCOMPLETE" });
        var services = ModApi.Services; var boarding = services.Boarding;
        foreach (var frame in Wait(NativeTravelReady, "Save fixture world readiness")) yield return frame;
        Require(boarding.GetTargets().Count == 0 && boarding.GetOperations().Count == 0, "Save fixture requires no prior dungeon targets or operations.");
        using var provider = services.Dungeons.AcquireProvider(Id + ".save");
        using var definition = provider.Register("saved-installation", new DungeonDefinition(1, "Saved installation", new DungeonLayout(new[]
        {
            new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, new[] { "hold" }),
            new DungeonCompartmentDefinition("hold", CompartmentType.CargoHold, new[] { "entry" })
        }), allowHazards: false, allowScheduledReinforcements: false));
        var dataType = NativeType("Source.Data.Persistable.DungeonLocationData");
        var kind = dataType.GetField("dungeonType")!;
        var get = NativeType("Behaviour.Dungeon.DungeonDefinition").GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { kind.FieldType }, null)!;
        var selected = Enum.GetValues(kind.FieldType).Cast<object>().First(value => get.Invoke(null, new[] { value }) is UnityEngine.Object native && native);
        var data = Activator.CreateInstance(dataType)!; kind.SetValue(data, selected);
        dataType.GetField("level")!.SetValue(data, 1);
        dataType.GetField("faction")!.SetValue(data, SpGet(NativeType("Source.Galaxy.Faction"), "player"));
        var root = new GameObject("Qualification saved dungeon");
        IBoardingController? controller = null; BoardingHandle? operation = null;
        DungeonSettlementSnapshot? settled = null;
        void OnSettlement(DungeonSettlementSnapshot snapshot) { if (operation != null && snapshot.Operation.Equals(operation)) settled = snapshot; }
        var records = new List<string>();
        void Record(string value) { if (records.Count < 32 && !records.Contains(value)) { records.Add(value); WriteAtomic("dungeon-save-diagnostic.txt", records); } }
        services.DungeonSettlement.Changed += OnSettlement;
        try
        {
            var unitType = NativeType("Behaviour.Unit.DungeonLocationUnit");
            unitType.GetMethod("Init", new[] { dataType })!.Invoke(root.AddComponent(unitType), new[] { data });
            foreach (var frame in Wait(() => boarding.GetTargets().Count == 1, "Save fixture target")) yield return frame;
            var target = boarding.GetTargets().Single().Handle;
            var attached = provider.Attach("saved-installation", target);
            Require(attached.Status == DungeonContentStatus.Attached && attached.OccurrenceId.HasValue, "Saved occurrence attachment failed.");
            var player = SpGet(NativeType("Source.Player.GamePlayer"), "current")!;
            object CurrentDonor() => SpGet(SpGet(NativeType("Source.Player.GamePlayer"), "current")!, "currentSpaceShip")!;
            Dictionary<string, int> CurrentCrew() => (Dictionary<string, int>)SpGet(SpGet(CurrentDonor(), "crewData")!, "crew")!;
            var originalDonor = CurrentDonor(); var donorId = SpGet(originalDonor, "guid") as string;
            Require(!string.IsNullOrEmpty(donorId), "Save donor has no persistent identity.");
            var before = new Dictionary<string, int>(CurrentCrew());
            var crew = before.OrderBy(pair => pair.Key, StringComparer.Ordinal).First(pair => pair.Value >= 6);
            Require(services.BoardingCommands.AcquireControl(Id, target, out controller).Admitted, "Save fixture control refused.");
            var started = controller!.Start(new BoardingCrewManifest(new[] { new KeyValuePair<string, int>(crew.Key, 6) }), new BoardingCommandOptions(), true);
            operation = started.Operation;
            Require(started.Admitted && operation != null, "Save fixture start failed.");
            foreach (var frame in Wait(() => boarding.GetOperation(operation!) is { Phase: BoardingPhase.Active } state && state.Compartments.Any(room => room.FriendlyCrew > 0), "Active operation before save")) yield return frame;
            bool Debited() => before.All(pair => (CurrentCrew().TryGetValue(pair.Key, out var count) ? count : 0) == pair.Value - (pair.Key == crew.Key ? 6 : 0)) && CurrentCrew().Keys.All(before.ContainsKey);
            Require(Debited(), "Pre-save crew debit mismatch.");
            // Fixture placement only: serialize this location in the real POI. Native loading must rebuild its representation.
            var poi = SpGet(player, "currentPointOfInterest")!;
            var persistables = (IList)SpGet(poi, "persistables")!;
            Require(!persistables.Contains(data), "Save fixture already present."); persistables.Add(data);
            Record("active-before-save occurrence-count=" + provider.GetOccurrences().Count + " crew=" + CurrentCrew().Values.Sum());
            Save("qa-dungeon-active", LifecycleEventKind.SaveSucceeded);
            var activeFile = Path.Combine(_saveRoot!, "qa-dungeon-active.save");
            var activeMeta = Path.Combine(_saveRoot!, "qa-dungeon-active.meta");
            var savedBody = File.ReadAllBytes(activeFile); var savedMeta = File.ReadAllBytes(activeMeta);
            var occurrence = provider.GetOccurrences().Single();
            bool SameOccurrence() => provider.GetOccurrences() is var occurrences && occurrences.Count == 1 && occurrences[0].Id == occurrence.Id && occurrences[0].DefinitionId.Equals(occurrence.DefinitionId) && occurrences[0].DefinitionVersion == occurrence.DefinitionVersion;
            bool Returned() => CurrentCrew().Count == before.Count && before.All(pair => CurrentCrew().TryGetValue(pair.Key, out var count) && count == pair.Value);
            IEnumerable<object?> RestoreAndReturn(string slot)
            {
                var oldOperation = operation; var priorDonor = CurrentDonor(); var priorSession = boarding.SessionId;
                operation = null; settled = null;
                foreach (var frame in LoadReady(slot)) yield return frame;
                foreach (var frame in Wait(NativeTravelReady, "Reloaded dungeon world readiness")) yield return frame;
                Require(controller?.IsActive != true && boarding.GetTarget(target) == null && (oldOperation == null || boarding.GetOperation(oldOperation) == null), "Reload retained old control or handles.");
                controller?.Dispose(); controller = null;
                bool Reconstructed()
                {
                    var targets = boarding.GetTargets();
                    Record(slot + " reloaded targets=" + targets.Count + " operations=" + boarding.GetOperations().Count + " occurrences=" + provider.GetOccurrences().Count + " crew=" + CurrentCrew().Values.Sum());
                    return targets.Count == 1 && targets[0].Operation != null;
                }
                foreach (var frame in Wait(Reconstructed, "Native reconstructed operation")) yield return frame;
                var restored = boarding.GetTargets().Single(); operation = restored.Operation;
                Require(restored.Handle.SessionId != priorSession && SameOccurrence(), "Occurrence or session identity did not roundtrip.");
                var restoredDonor = CurrentDonor();
                Require(!ReferenceEquals(priorDonor, restoredDonor) && (string?)SpGet(restoredDonor, "guid") == donorId, "Reload did not reconstruct the same donor identity.");
                Require(Debited(), "Reload duplicated or lost assigned crew.");
                Require(services.BoardingCommands.AcquireControl(Id, restored.Handle, out controller).Admitted, "Restored control refused.");
                foreach (var frame in Wait(() => boarding.GetOperation(operation!)?.Phase == BoardingPhase.Active, "Restored active phase")) yield return frame;
                Require(controller!.Retreat().Admitted, "Restored retreat refused.");
                foreach (var frame in Wait(() => settled is { CrewReturnSettled: true, CrewCountsObserved: true }, "Restored crew return settlement")) yield return frame;
                Require(settled!.NativeOutcome == "FriendlyExtracted" && settled.Casualties.Values.All(count => count == 0), "Unexpected restored outcome or casualties.");
                Require(ReferenceEquals(restoredDonor, CurrentDonor()), "Restored donor changed during return.");
                Require(CurrentCrew().Count == before.Count && before.All(pair => CurrentCrew().TryGetValue(pair.Key, out var count) && count == pair.Value), "Restored crew return did not reconcile.");
                foreach (var frame in Wait(() => boarding.GetTarget(restored.Handle) is { Operation: null }, "Restored operation retirement")) yield return frame;
                Record(slot + " returned crew=" + CurrentCrew().Values.Sum() + " outcome=" + settled.NativeOutcome);
                target = restored.Handle;
            }
            foreach (var frame in CheckDungeonSaveFailures(() => controller?.IsActive == true && Debited() && SameOccurrence() && boarding.GetOperation(operation!)?.Phase == BoardingPhase.Active)) yield return frame;
            Require(File.ReadAllBytes(activeFile).SequenceEqual(savedBody) && File.ReadAllBytes(activeMeta).SequenceEqual(savedMeta), "Faulted writes altered the successful checkpoint.");
            Save("qa-dungeon-copy", LifecycleEventKind.SaveSucceeded);
            Require(Debited() && SameOccurrence(), "Save-as changed live occurrence or crew.");
            var activeHandle = operation;
            foreach (var frame in LoadReady("fixture-b")) yield return frame;
            foreach (var frame in Wait(NativeTravelReady, "Unrelated slot readiness")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            Require(controller?.IsActive != true && boarding.GetTarget(target) == null && boarding.GetOperation(activeHandle!) == null, "Slot switch retained old runtime identity.");
            Require(provider.GetOccurrences().Count == 0 && boarding.GetTargets().Count == 0 && boarding.GetOperations().Count == 0, "Occurrence or operation leaked into another slot.");
            Record("unrelated-slot occurrences=0 targets=0 operations=0");
            controller?.Dispose(); controller = null; operation = null; settled = null;
            foreach (var frame in RestoreAndReturn("qa-dungeon-copy")) yield return frame;
            Save("qa-dungeon-active", LifecycleEventKind.SaveSucceeded);
            var retiredHandle = operation; var settledSession = boarding.SessionId; var settledDonor = CurrentDonor();
            foreach (var frame in LoadReady("qa-dungeon-active")) yield return frame;
            foreach (var frame in Wait(() => NativeTravelReady() && boarding.GetTargets().Count == 1, "Resolved checkpoint native target")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            Require(controller?.IsActive != true && boarding.GetTarget(target) == null && boarding.GetOperation(retiredHandle!) == null, "Resolved reload retained old handles.");
            controller?.Dispose(); controller = null; operation = null; settled = null;
            Require(boarding.SessionId != settledSession && SameOccurrence() && boarding.GetOperations().Count == 0 && boarding.GetTargets().Single().Operation == null, "Resolved checkpoint restarted its operation.");
            Require(!ReferenceEquals(settledDonor, CurrentDonor()) && (string?)SpGet(CurrentDonor(), "guid") == donorId && Returned(), "Resolved reload duplicated crew or changed recipient.");
            target = boarding.GetTargets().Single().Handle;
            Record("resolved-reload crew=" + CurrentCrew().Values.Sum() + " operations=0");
            // Deliberately restore the earlier bytes at the same path while no operation is active.
            // This is an explicit copied-save rollback, not a cross-file atomicity claim.
            File.WriteAllBytes(activeFile, savedBody); File.WriteAllBytes(activeMeta, savedMeta);
            Require(File.ReadAllBytes(activeFile).SequenceEqual(savedBody) && File.ReadAllBytes(activeMeta).SequenceEqual(savedMeta), "Rollback fixture bytes did not match the active checkpoint.");
            Record("same-path rollback bytes verified");
            foreach (var frame in RestoreAndReturn("qa-dungeon-active")) yield return frame;
            WriteAtomic("dungeon-save.txt", new[] { "PASS", "dungeon-save-v3", "save-failures-save-as-slot-switch-resolved-reload-in-place-rollback" });
        }
        finally
        {
            services.DungeonSettlement.Changed -= OnSettlement;
            try { if (controller?.IsActive == true) { controller.Retreat(); controller.CancelApproach(); } }
            finally { controller?.Dispose(); if (root) UnityEngine.Object.Destroy(root); }
        }
    }
}
