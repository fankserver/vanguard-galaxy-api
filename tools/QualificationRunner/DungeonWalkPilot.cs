using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine.InputSystem;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckDungeonWalk(Mouse mouse)
    {
        WriteAtomic("dungeon-walk.txt", new[] { "INCOMPLETE" });
        var combatMarker = Path.Combine(_root!, "dungeon-combat.enabled");
        var combat = File.Exists(combatMarker);
        Require(!combat || File.ReadAllText(combatMarker) == "dungeon-combat-v1", "Invalid combat mode marker.");
        var reward = File.Exists(Path.Combine(_root!, "dungeon-reward.enabled"));
        Require(!reward || (combat && File.ReadAllText(Path.Combine(_root!, "dungeon-reward.enabled")) == "dungeon-reward-v1"), "Invalid reward mode marker.");
        if (reward) SeedRewardCrew();
        using var rewards = reward ? new DungeonRewardProbe() : null;
        using var policies = combat ? new DungeonCombatProbe() : null;
        var boarding = ModApi.Services.Boarding;
        var target = ModApi.Services.DungeonPanel.Current!.Target.Handle;
        var playerType = NativeType("Source.Player.GamePlayer");
        var player = SpGet(playerType, "current")!;
        var donor = SpGet(player, "currentSpaceShip")!;
        Dictionary<string, int> Roster() => (Dictionary<string, int>)SpGet(SpGet(donor, "crewData")!, "crew")!;
        var before = new Dictionary<string, int>(Roster());
        Dictionary<string, int> Prisoners() => (Dictionary<string, int>)SpGet(SpGet(donor, "prisonerData")!, "prisoners")!;
        var prisonersBefore = new Dictionary<string, int>(Prisoners());
        var candidate = before.OrderBy(pair => pair.Key, StringComparer.Ordinal).FirstOrDefault(pair => pair.Value >= 6 && (!reward || pair.Key == "Marine"));
        Require(candidate.Key != null, "Active walk fixture requires six existing crew of one type.");
        var manifest = new BoardingCrewManifest(new[] { new KeyValuePair<string, int>(candidate.Key!, 6) });
        var commands = ModApi.Services.BoardingCommands;
        IBoardingController? controller = null; BoardingHandle? operation = null;
        var settlement = ModApi.Services.DungeonSettlement;
        DungeonSettlementSnapshot? settledSnapshot = null;
        var inventoryDelivered = 0;
        void OnDelivery(BoardingEvent message)
        {
            if (reward && operation != null && message.Operation?.Handle.Equals(operation) == true && message.Delivery?.Route == BoardingDeliveryRoute.Inventory) inventoryDelivered += message.Delivery.Quantity;
        }
        void OnSettlement(DungeonSettlementSnapshot snapshot)
        {
            if (operation != null && snapshot.Operation.Equals(operation)) settledSnapshot = snapshot;
        }
        var records = new List<string>(); string? last = null;
        bool ObserveActive()
        {
            var snapshot = operation == null ? null : boarding.GetOperation(operation);
            var state = "phase=" + snapshot?.Phase + " rooms=" + snapshot?.Compartments.Count + " crew=" + snapshot?.Compartments.Sum(room => room.FriendlyCrew);
            if (state != last && records.Count < 24) { records.Add(state); last = state; WriteAtomic("dungeon-walk-diagnostic.txt", records); }
            // Native AddCrew precedes the simulation tick that reveals occupied rooms.
            return snapshot?.Phase == BoardingPhase.Active && snapshot.Compartments.Any(room => room.Kind == "Airlock" && room.FriendlyCrew > 0);
        }
        settlement.Changed += OnSettlement;
        boarding.Changed += OnDelivery;
        try
        {
            Require(commands.AcquireControl(Id, target, out controller).Admitted && controller != null, "Walk control refused.");
            var started = controller!.Start(manifest, new BoardingCommandOptions(), true);
            Require(started.Admitted && started.Operation != null, "Walk start refused: " + started.Status);
            operation = started.Operation;
            foreach (var frame in Wait(ObserveActive, "Actual installation crew arrival and active simulation")) yield return frame;
            var active = boarding.GetOperation(operation!)!;
            Require(!active.Autonomous && !active.AutoMove && active.Compartments.Any(room => room.Kind == "Airlock" && room.FriendlyCrew > 0), "Manual crew arrival was not observed in the airlock.");
            var during = Roster();
            Require(during.Keys.All(before.ContainsKey) && before.All(pair => (during.TryGetValue(pair.Key, out var count) ? count : 0) == pair.Value - (manifest.Crew.TryGetValue(pair.Key, out var sent) ? sent : 0)), "Active crew debit did not match the requested manifest exactly once.");
            records.Add("active-debit=" + manifest.Count + " crew=" + candidate.Key + " before=" + before[candidate.Key!] + " active=" + (during.TryGetValue(candidate.Key!, out var activeCount) ? activeCount : 0));
            WriteAtomic("dungeon-walk-diagnostic.txt", records);
            if (combat)
            {
                foreach (var frame in CheckDungeonVictory(controller, operation!, mouse, policies!, reward)) yield return frame;
            }
            else Require(controller.Retreat().Admitted, "Active retreat refused.");
            foreach (var frame in Wait(() => settledSnapshot is { CrewReturnSettled: true, CrewCountsObserved: true }, "Actual returning crew settlement")) yield return frame;
            var settled = settledSnapshot!;
            Require(settled.NativeOutcome == (combat ? "FriendlyVictory" : "FriendlyExtracted") && !settled.CaptureApplied, "Retreat produced an unexpected outcome or capture.");
            Require(ReferenceEquals(player, SpGet(playerType, "current")) && ReferenceEquals(donor, SpGet(player, "currentSpaceShip")), "Walk donor identity changed.");
            var after = Roster();
            records.Add("casualties=" + string.Join(",", settled.Casualties.Select(pair => pair.Key + ":" + pair.Value)) + " prisoners-delivered=" + string.Join(",", settled.PrisonersDelivered.Select(pair => pair.Key + ":" + pair.Value)));
            WriteAtomic("dungeon-walk-diagnostic.txt", records);
            Require(settled.Casualties.All(pair => before.ContainsKey(pair.Key)), "Unexpected casualty identity.");
            var prisonersAfter = Prisoners();
            var prisonerKeys = prisonersBefore.Keys.Concat(prisonersAfter.Keys).Concat(settled.PrisonersDelivered.Keys).Distinct();
            Require(prisonerKeys.All(key => (prisonersAfter.TryGetValue(key, out var actual) ? actual : 0) == (prisonersBefore.TryGetValue(key, out var prior) ? prior : 0) + (settled.PrisonersDelivered.TryGetValue(key, out var delivered) ? delivered : 0)), "Observed prisoner delivery did not reconcile with the same donor's brig.");
            Require(after.Keys.All(before.ContainsKey) && before.All(pair => (after.TryGetValue(pair.Key, out var count) ? count : 0) == pair.Value - (settled.Casualties.TryGetValue(pair.Key, out var lost) ? lost : 0)), "Returning crew did not reconcile with observed casualties.");
            records.Add("settlement=" + settled.NativeOutcome + " after=" + (after.TryGetValue(candidate.Key!, out var returnedCount) ? returnedCount : 0) + " casualties=" + settled.Casualties.Values.Sum());
            WriteAtomic("dungeon-walk-diagnostic.txt", records);
            foreach (var frame in Wait(() => boarding.GetTarget(target) is { Operation: null }, "Walk operation retirement")) yield return frame;
            if (reward)
            {
                var delivered = rewards!.Verify();
                Require(inventoryDelivered == 4, "Inventory delivery events did not reconcile with actual cargo.");
                WriteAtomic("dungeon-reward.txt", new[] { "PASS", "dungeon-reward-v1", "authored-two-multiplied-four-cargo-delivered" });
                WriteAtomic("dungeon-reward-diagnostic.txt", new[] { "before=" + delivered.Before + " after=" + delivered.After + " inventory-events=" + inventoryDelivered + " loot-policy-calls=" + rewards.LootCalls });
            }
            WriteAtomic("dungeon-walk.txt", new[] { "PASS", combat ? "dungeon-combat-v1" : "dungeon-walk-v1", combat ? "manual-victory-choice-extraction" : "manual-arrival-retreat-settlement", "donor-crew-reconciled" });
        }
        finally
        {
            try
            {
                if (controller?.IsActive == true)
                {
                    var observed = operation ?? boarding.GetTarget(target)?.Operation;
                    var phase = observed == null ? null : boarding.GetOperation(observed)?.Phase;
                    BoardingCommandResult? cleanup = null;
                    if (operation == null || phase == BoardingPhase.Approaching || phase == BoardingPhase.AwaitingLanding) cleanup = controller.CancelApproach();
                    else if (phase == BoardingPhase.Active) cleanup = controller.Retreat();
                    if (cleanup != null) WriteAtomic("dungeon-walk-cleanup.txt", new[] { cleanup.Status.ToString(), cleanup.Detail, "Failure-path command admission is not proof of settlement." });
                }
            }
            finally
            {
                boarding.Changed -= OnDelivery;
                settlement.Changed -= OnSettlement;
                controller?.Dispose();
            }
        }
    }
}
