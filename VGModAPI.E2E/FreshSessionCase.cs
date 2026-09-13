using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.E2E;

internal static class FreshSessionCase
{
    internal const string Id = "fresh-session";

    internal static IReadOnlyList<TestStep> Steps(ILifecycleService lifecycle, List<LifecycleEvent> events) => new[]
    {
        new TestStep("main menu", "Behaviour.UI.MainMenuUI.instance", NativeSession.MenuReady),
        new TestStep("open normal New Game", "MainMenuUI.StartGame", () =>
        {
            var availability = lifecycle.SessionTracking.Availability;
            if (!availability.IsAvailable)
                throw new InvalidOperationException("SessionTracking: " + availability.Reason + ": " + availability.Detail);
            NativeSession.OpenNewGameWizard();
            return true;
        }),
        new TestStep("complete normal New Game", "NewGame.SubmitInput / SaveInputs / GameManager.StartNewGame",
            NativeSession.AdvanceNewGameWizard),
        new TestStep("initialize gameplay", "GameplayManager.Start / lifecycle SessionTracking", () =>
        {
            NativeSession.RequireEphemeral();
            var failure = events.FirstOrDefault(e => e.Kind == LifecycleEventKind.SessionStartFailed
                || e.Kind == LifecycleEventKind.SessionInvalidated);
            if (failure != null) throw new InvalidOperationException(failure.Kind + ": " + failure.Detail);
            return NativeSession.Initialized() && lifecycle.CurrentSession?.Phase == SessionPhase.GameplayInitialized;
        }),
        new TestStep("assert new-game lifecycle", "NewGame.SaveInputs / LoadScenesOnStartGame / GameplayManager.Start", () =>
        {
            NativeSession.RequireEphemeral();
            var session = lifecycle.CurrentSession;
            if (session == null || session.Origin != SessionOrigin.NewGame || session.SavePath != null)
                throw new InvalidOperationException("Expected a new unsaved gameplay session.");
            var phases = events.Where(e => e.Kind == LifecycleEventKind.SessionStarting
                || e.Kind == LifecycleEventKind.PlayerReady || e.Kind == LifecycleEventKind.GameplayInitialized).ToArray();
            var expected = new[] { LifecycleEventKind.SessionStarting, LifecycleEventKind.PlayerReady, LifecycleEventKind.GameplayInitialized };
            if (!phases.Select(e => e.Kind).SequenceEqual(expected) || phases.Any(e => e.Session?.Id != session.Id))
                throw new InvalidOperationException("Expected exactly one ordered lifecycle for the same session: "
                    + string.Join(", ", phases.Select(e => e.Kind)));
            if (events.Any(e => e.Kind == LifecycleEventKind.SaveSucceeded))
                throw new InvalidOperationException("An ephemeral player unexpectedly saved.");
            return true;
        })
    };
}
