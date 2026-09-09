using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.InputSystem;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private sealed class DungeonCombatProbe : IDisposable
    {
        private readonly IBoardingCombatProvider _provider;
        private readonly List<IDisposable> _leases = new();
        internal int HealthCalls, PowerCalls, WrongScope;
        internal DungeonCombatProbe()
        {
            _provider = ModApi.Services.BoardingCombat.AcquireProvider(Id + ".combat");
            try
            {
                _leases.Add(_provider.RegisterMultiplier("health", BoardingRuleScope.Installations, BoardingCombatPolicyKind.InitialHealth,
                    context => { if (context.Side != BoardingCombatSide.Attackers) return 1; HealthCalls++; return 5; }));
                _leases.Add(_provider.RegisterMultiplier("power", BoardingRuleScope.Installations, BoardingCombatPolicyKind.Power,
                    context => { if (context.Side != BoardingCombatSide.Attackers) return 1; PowerCalls++; return 2; }));
                _leases.Add(_provider.RegisterMultiplier("excluded", BoardingRuleScope.Ships, BoardingCombatPolicyKind.Power,
                    context => { if (context.Encounter.Kind == BoardingEncounterKind.Installation) WrongScope++; return 1; }));
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var lease in _leases) lease.Dispose(); _leases.Clear(); _provider.Dispose(); }
    }
    private IEnumerable<object?> CheckDungeonVictory(IBoardingController controller, BoardingHandle operation, Mouse mouse, DungeonCombatProbe policies)
    {
        var boarding = ModApi.Services.Boarding;
        var tactics = ModApi.Services.BoardingTactics;
        var records = new List<string>(); string? last = null;
        Require(controller.SetOptions(new BoardingCommandOptions(autoMove: true)).Admitted, "Combat movement options refused.");
        bool Victory()
        {
            var snapshot = boarding.GetOperation(operation);
            var state = "phase=" + snapshot?.Phase + " outcome=" + snapshot?.Outcome + " rooms=" + snapshot?.Compartments.Count + " hostiles=" + snapshot?.Compartments.Sum(room => room.HostileCrew);
            if (state != last && records.Count < 32) { records.Add(state); last = state; WriteAtomic("dungeon-combat-diagnostic.txt", records); }
            return snapshot?.Outcome == "FriendlyVictory";
        }
        foreach (var frame in Wait(Victory, "Native authored encounter victory")) yield return frame;
        Require(policies.HealthCalls > 0 && policies.PowerCalls > 0 && policies.WrongScope == 0, "Combat policy invocation/scope checks failed.");
        foreach (var frame in Wait(() => DungeonProbeButton("Leave shipment") != null, "Author's live leave choice")) yield return frame;
        foreach (var frame in DungeonClick(mouse, DungeonProbeButton("Leave shipment")!.transform)) yield return frame;
        foreach (var frame in Wait(() => DungeonProbeButton("Leave shipment") == null, "Consumed author choice removal")) yield return frame;
        Require(boarding.GetOperation(operation)?.Outcome == "FriendlyVictory", "Choice disappearance was not during the same live victory.");
        foreach (var frame in Wait(() => tactics.GetSnapshot(operation)?.CanRequestExtraction == true, "Victory extraction readiness")) yield return frame;
        Require(tactics.Execute(controller, new BoardingTacticalRequest(BoardingTacticalAction.RequestExtraction)).Admitted, "Tactical extraction request refused.");
        foreach (var frame in Wait(() => tactics.GetSnapshot(operation)?.AwaitingExtraction == true, "Requested extraction observation")) yield return frame;
        Require(tactics.Execute(controller, new BoardingTacticalRequest(BoardingTacticalAction.ConfirmExtraction)).Admitted, "Tactical extraction confirmation refused.");
        records.Add("health-calls=" + policies.HealthCalls + " power-calls=" + policies.PowerCalls + " excluded-scope=" + policies.WrongScope + " leave-choice-removed=true extraction-confirmed=true");
        WriteAtomic("dungeon-combat-diagnostic.txt", records);
    }
}
