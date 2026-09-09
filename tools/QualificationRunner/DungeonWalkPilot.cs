using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckDungeonWalk()
    {
        WriteAtomic("dungeon-walk.txt", new[] { "INCOMPLETE" });
        var boarding = ModApi.Services.Boarding;
        var target = ModApi.Services.DungeonPanel.Current!.Target.Handle;
        var playerType = NativeType("Source.Player.GamePlayer");
        var player = SpGet(playerType, "current")!;
        var donor = SpGet(player, "currentSpaceShip")!;
        Dictionary<string, int> Roster() => (Dictionary<string, int>)SpGet(SpGet(donor, "crewData")!, "crew")!;
        var before = new Dictionary<string, int>(Roster());
        var candidate = before.OrderBy(pair => pair.Key, StringComparer.Ordinal).FirstOrDefault(pair => pair.Value >= 6);
        Require(candidate.Key != null, "Active walk fixture requires six existing crew of one type.");
        var manifest = new BoardingCrewManifest(new[] { new KeyValuePair<string, int>(candidate.Key!, 6) });
        var commands = ModApi.Services.BoardingCommands;
        IBoardingController? controller = null; BoardingHandle? operation = null;
        var settlement = ModApi.Services.DungeonSettlement;
        DungeonSettlementSnapshot? settledSnapshot = null;
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
        try
        {
            Require(commands.AcquireControl(Id, target, out controller).Admitted && controller != null, "Walk control refused.");
            var started = controller!.Start(manifest, new BoardingCommandOptions(), true);
            Require(started.Admitted && started.Operation != null, "Walk start refused: " + started.Status);
            operation = started.Operation;
            foreach (var frame in Wait(ObserveActive, "Actual installation crew arrival and active simulation")) yield return frame;
            var active = boarding.GetOperation(operation!)!;
            Require(!active.Autonomous && !active.AutoMove && active.Compartments.Any(room => room.Kind == "Airlock" && room.FriendlyCrew > 0), "Manual crew arrival was not observed in the airlock.");
            Require(controller.Retreat().Admitted, "Active retreat refused.");
            foreach (var frame in Wait(() => settledSnapshot is { CrewReturnSettled: true, CrewCountsObserved: true }, "Actual returning crew settlement")) yield return frame;
            var settled = settledSnapshot!;
            Require(settled.NativeOutcome == "FriendlyExtracted" && !settled.CaptureApplied, "Retreat produced an unexpected outcome or capture.");
            Require(ReferenceEquals(player, SpGet(playerType, "current")) && ReferenceEquals(donor, SpGet(player, "currentSpaceShip")), "Walk donor identity changed.");
            var after = Roster();
            Require(settled.Casualties.All(pair => before.ContainsKey(pair.Key)) && settled.PrisonersDelivered.Values.All(count => count == 0), "Unexpected casualty or prisoner identity.");
            Require(after.Keys.All(before.ContainsKey) && before.All(pair => (after.TryGetValue(pair.Key, out var count) ? count : 0) == pair.Value - (settled.Casualties.TryGetValue(pair.Key, out var lost) ? lost : 0)), "Returning crew did not reconcile with observed casualties.");
            foreach (var frame in Wait(() => boarding.GetTarget(target) is { Operation: null }, "Walk operation retirement")) yield return frame;
            WriteAtomic("dungeon-walk.txt", new[] { "PASS", "dungeon-walk-v1", "manual-arrival-retreat-settlement", "donor-crew-reconciled" });
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
                settlement.Changed -= OnSettlement;
                controller?.Dispose();
            }
        }
    }
}
