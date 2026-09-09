using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckDungeonCommandAdmission()
    {
        WriteAtomic("dungeon-commands.txt", new[] { "INCOMPLETE" });
        var commands = ModApi.Services.BoardingCommands; var boarding = ModApi.Services.Boarding;
        var target = ModApi.Services.DungeonPanel.Current!.Target.Handle;
        Require(boarding.GetTarget(target) is { Kind: BoardingEncounterKind.Installation, Operation: null }, "Command fixture must be an idle installation.");
        // Read-only fixture inventory inspection; all mutations below use public commands.
        var player = SpGet(NativeType("Source.Player.GamePlayer"), "current")!;
        var shipData = SpGet(player, "currentSpaceShip")!;
        var crewData = SpGet(shipData, "crewData")!;
        var crew = (Dictionary<string, int>)SpGet(crewData, "crew")!;
        var before = new Dictionary<string, int>(crew);
        var docking = SpGet(shipData, "dockingState");
        var candidate = before.OrderBy(pair => pair.Key, StringComparer.Ordinal).FirstOrDefault(pair => pair.Value > 0);
        Require(candidate.Key != null, "Command fixture requires existing crew.");
        var manifest = new BoardingCrewManifest(new[] { new KeyValuePair<string, int>(candidate.Key!, 1) });
        IBoardingController? controller = null; IBoardingController? contender = null;
        var pendingCancel = false; var records = new List<string>();
        void Check(string name, BoardingCommandResult result, BoardingCommandStatus expected)
        {
            records.Add(name + "=" + result.Status + " detail=" + result.Detail);
            WriteAtomic("dungeon-command-diagnostic.txt", records);
            Require(result.Status == expected, "Dungeon command " + name + " returned " + result.Status);
        }
        try
        {
            Check("acquire", commands.AcquireControl(Id, target, out controller), BoardingCommandStatus.Admitted);
            Require(controller != null && controller.IsActive, "No active admitted controller.");
            Check("conflict", commands.AcquireControl(Id + ".contender", target, out contender), BoardingCommandStatus.ControlConflict);
            Require(contender == null, "Conflicting controller was returned.");
            // No yield from Start through CancelApproach: native approach Tick cannot dock, walk or launch.
            pendingCancel = true;
            var start = controller!.Start(manifest, new BoardingCommandOptions(), allowFactionConsequences: true);
            Check("start", start, BoardingCommandStatus.Admitted);
            Require(start.Operation != null && boarding.GetOperation(start.Operation) is { Phase: BoardingPhase.Approaching, Autonomous: false }, "Manual approach was not observed.");
            Check("duplicate", controller.Start(manifest, new BoardingCommandOptions(), true), BoardingCommandStatus.OperationExists);
            Check("options", controller.SetOptions(new BoardingCommandOptions(BoardingAmmunition.Hollow, BoardingStealth.Silent, true)), BoardingCommandStatus.Admitted);
            var resumed = controller.Resume();
            Check("resume-existing", resumed, BoardingCommandStatus.Admitted);
            Require(start.Operation!.Equals(resumed.Operation), "Resume replaced the live operation identity.");
            Check("reinforce-before-simulation", controller.Reinforce(manifest), BoardingCommandStatus.WrongPhase);
            Check("retreat-before-simulation", controller.Retreat(), BoardingCommandStatus.WrongPhase);
            Check("extract-before-simulation", controller.RequestExtraction(), BoardingCommandStatus.WrongPhase);
            Check("confirm-before-simulation", controller.ConfirmExtraction(), BoardingCommandStatus.WrongPhase);
            Check("cancel", controller.CancelApproach(), BoardingCommandStatus.Admitted);
            pendingCancel = false;
        }
        finally
        {
            try { if (pendingCancel && controller?.IsActive == true) Check("cleanup-cancel", controller.CancelApproach(), BoardingCommandStatus.Admitted); }
            finally { contender?.Dispose(); controller?.Dispose(); }
        }
        foreach (var frame in Wait(() => boarding.GetTarget(target) is { Operation: null }, "Cancelled operation retirement")) yield return frame;
        Require(ReferenceEquals(player, SpGet(NativeType("Source.Player.GamePlayer"), "current")) && ReferenceEquals(shipData, SpGet(player, "currentSpaceShip")), "Command donor changed.");
        var after = (Dictionary<string, int>)SpGet(SpGet(shipData, "crewData")!, "crew")!;
        Require(before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var count) && count == pair.Value), "Pre-approach commands changed crew inventory.");
        Require(Equals(docking, SpGet(shipData, "dockingState")), "Pre-approach commands changed docking state.");
        WriteAtomic("dungeon-commands.txt", new[] { "PASS", "dungeon-commands-v1", "control-start-options-refusals-cancel-before-tick", "crew-and-docking-preserved" });
    }
}
