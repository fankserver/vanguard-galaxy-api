using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.E2E;

/// <summary>
/// The normal new-game bootstrap shared by every live example case: load the example plugin,
/// run the real five-step New Game wizard (ephemeral player), enter gameplay, and finish the
/// opening dialogue so onward HUD interaction is reliable. Mirrors what the PocketWorlds case
/// does inline, factored out so the remaining cases stay focused on their own behaviour.
/// </summary>
internal static class LiveBoot
{
    internal const string MenuBinding = "MainMenuUI.StartGame";
    internal const string WizardBinding = "NewGame.SubmitInput / SaveInputs / GameManager.StartNewGame";
    internal const string DialogueBinding = "DialogueManager.IsDialogueOpen / NextOrFinish";

    /// <summary>Steps up to and including gameplay initialization plus opening-dialogue finish.</summary>
    internal static IReadOnlyList<TestStep> Steps(
        string pluginId, ILifecycleService lifecycle, List<LifecycleEvent> events)
    {
        var sawOpeningDialogue = false;
        return new[]
        {
            new TestStep("main menu and example load", "Chainloader.PluginInfos[" + pluginId + "]", () =>
            {
                if (!NativeSession.MenuReady()) return false;
                return NativeGameplay.PluginInstance(pluginId) != null;
            }),
            new TestStep("open normal New Game", MenuBinding, () =>
            {
                var availability = lifecycle.SessionTracking.Availability;
                if (!availability.IsAvailable)
                    throw new InvalidOperationException("SessionTracking: " + availability.Reason + ": " + availability.Detail);
                NativeSession.OpenNewGameWizard();
                return true;
            }),
            new TestStep("complete normal New Game", WizardBinding, NativeSession.AdvanceNewGameWizard),
            new TestStep("initialize normal gameplay", "GameplayManager.Start / lifecycle SessionTracking", () =>
            {
                NativeSession.RequireEphemeral();
                var failure = events.FirstOrDefault(e => e.Kind == LifecycleEventKind.SessionStartFailed
                    || e.Kind == LifecycleEventKind.SessionInvalidated);
                if (failure != null) throw new InvalidOperationException(failure.Kind + ": " + failure.Detail);
                if (!NativeSession.Initialized() || lifecycle.CurrentSession?.Phase != SessionPhase.GameplayInitialized) return false;
                NativeGameplay.Screenshot("normal-gameplay-start");
                return true;
            }),
            new TestStep("finish opening dialogue", DialogueBinding, () =>
            {
                if (!sawOpeningDialogue)
                {
                    if (!NativeGameplay.DialogueOpen()) return false;
                    sawOpeningDialogue = true;
                }
                if (!NativeGameplay.DialogueOpen()) return true;
                NativeGameplay.AdvanceDialogue();
                return false;
            }),
        };
    }
}
