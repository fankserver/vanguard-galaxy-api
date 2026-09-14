using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.E2E;

/// <summary>Normal new-game bootstrap shared by live example cases.</summary>
internal static class LiveBoot
{
    internal const string MenuBinding = "MainMenuUI.StartGame";
    internal const string WizardBinding = "NewGame.SubmitInput / SaveInputs / GameManager.StartNewGame";
    internal const string DialogueBinding = "DialogueManager.IsDialogueOpen / NextOrFinish";

    internal static IReadOnlyList<TestStep> Steps(
        string pluginId, ILifecycleService lifecycle, List<LifecycleEvent> events)
    {
        var sawOpeningDialogue = false;
        return new[]
        {
            new TestStep("main menu and example load", "Chainloader.PluginInfos[" + pluginId + "]", 45, () =>
            {
                var menuReady = NativeSession.MenuReady();
                var loaded = NativeGameplay.PluginInstance(pluginId) != null;
                return menuReady && loaded ? StepResult.Pass("menu ready; plugin loaded")
                    : StepResult.Wait($"menuReady={menuReady}; pluginLoaded={loaded}");
            }),
            TestStep.Action("open normal New Game", MenuBinding, 15, () =>
            {
                var availability = lifecycle.SessionTracking.Availability;
                if (!availability.IsAvailable)
                    return StepResult.Fail("SessionTracking: " + availability.Reason + ": " + availability.Detail);
                NativeSession.OpenNewGameWizard();
                return StepResult.Pass("New Game wizard requested");
            }),
            new TestStep("complete normal New Game", WizardBinding, 60, () =>
                NativeSession.AdvanceNewGameWizard() ? StepResult.Pass("wizard submitted")
                    : StepResult.Wait("wizard is still loading or advancing through its pages")),
            new TestStep("initialize normal gameplay", "GameplayManager.Start / lifecycle SessionTracking", 90, () =>
            {
                NativeSession.RequireEphemeral();
                var failure = events.FirstOrDefault(e => e.Kind == LifecycleEventKind.SessionStartFailed
                    || e.Kind == LifecycleEventKind.SessionInvalidated);
                if (failure != null) return StepResult.Fail(failure.Kind + ": " + failure.Detail);
                var native = NativeSession.Initialized();
                var phase = lifecycle.CurrentSession?.Phase;
                if (!native || phase != SessionPhase.GameplayInitialized)
                    return StepResult.Wait($"nativeInitialized={native}; lifecyclePhase={phase?.ToString() ?? "null"}; events={events.Count}");
                NativeGameplay.Screenshot("normal-gameplay-start");
                return StepResult.Pass("native gameplay and lifecycle session initialized");
            }),
            new TestStep("finish opening dialogue", DialogueBinding, 30, context =>
            {
                if (NativeGameplay.DialogueOpen())
                {
                    sawOpeningDialogue = true;
                    NativeGameplay.AdvanceDialogue();
                    return StepResult.Wait($"visible opening dialogue is being advanced; elapsed={context.ElapsedSeconds:0.0}s");
                }
                if (sawOpeningDialogue) return StepResult.Pass("opening dialogue closed");
                if (context.ElapsedSeconds < 20)
                    return StepResult.Wait($"no visible dialogue yet; grace={context.ElapsedSeconds:0.0}/20.0s");
                return StepResult.Pass("no opening dialogue appeared during the 20s grace period");
            }),
        };
    }
}
