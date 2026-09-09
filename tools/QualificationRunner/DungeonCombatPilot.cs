using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
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
    private IEnumerable<object?> DungeonClickCurrentChoice(Mouse mouse, string label)
    {
        var deadline = Time.realtimeSinceStartup + 90;
        while (Time.realtimeSinceStartup < deadline)
        {
            var button = DungeonProbeButton(label);
            if (!button || !DungeonPointerReady(button!.transform)) { yield return null; continue; }
            var point = ForgePointerPoint(button!.transform);
            InputSystem.QueueStateEvent(mouse, new MouseState { position = point });
            yield return null; yield return null;
            // A presenter may rebuild rows while the pointer moves. No press has been sent yet.
            if (!button || DungeonProbeButton(label) != button) continue;
            ForgePointerPoint(button!.transform, point);
            try
            {
                InputSystem.QueueStateEvent(mouse, new MouseState { position = point }.WithButton(MouseButton.Left));
                yield return null; yield return null;
            }
            finally { InputSystem.QueueStateEvent(mouse, new MouseState { position = point }); }
            yield return null; yield return null;
            yield break; // Never retry after a press: downstream assertions must prove its outcome.
        }
        throw new InvalidOperationException("Timed out resolving a stable dungeon choice before pointer press.");
    }
    private IEnumerable<object?> CheckDungeonVictory(IBoardingController controller, BoardingHandle operation, Mouse mouse, DungeonCombatProbe policies, bool reward)
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
        var choice = reward ? "Recover shipment" : "Leave shipment";
        foreach (var frame in Wait(() => DungeonProbeButton(choice) != null, "Author's live shipment choice")) yield return frame;
        foreach (var frame in DungeonClickCurrentChoice(mouse, choice)) yield return frame;
        foreach (var frame in Wait(() => DungeonProbeButton(choice) == null, "Consumed author choice removal")) yield return frame;
        Require(boarding.GetOperation(operation)?.Outcome == "FriendlyVictory", "Choice disappearance was not during the same live victory.");
        foreach (var frame in Wait(() => tactics.GetSnapshot(operation)?.CanRequestExtraction == true, "Victory extraction readiness")) yield return frame;
        Require(tactics.Execute(controller, new BoardingTacticalRequest(BoardingTacticalAction.RequestExtraction)).Admitted, "Tactical extraction request refused.");
        foreach (var frame in Wait(() => tactics.GetSnapshot(operation)?.AwaitingExtraction == true, "Requested extraction observation")) yield return frame;
        Require(tactics.Execute(controller, new BoardingTacticalRequest(BoardingTacticalAction.ConfirmExtraction)).Admitted, "Tactical extraction confirmation refused.");
        records.Add("health-calls=" + policies.HealthCalls + " power-calls=" + policies.PowerCalls + " excluded-scope=" + policies.WrongScope + " choice=" + choice + " choice-removed=true extraction-confirmed=true");
        WriteAtomic("dungeon-combat-diagnostic.txt", records);
    }
}
