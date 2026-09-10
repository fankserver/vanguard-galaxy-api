using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine.SceneManagement;
using VGModAPI.Core;

namespace VGModAPI;

public sealed partial class Plugin
{
    private DialogueService? _dialogueService;
    private Harmony? _dialogueHarmony;
    private void InitializeDialogue()
    {
        _dialogueService = new DialogueService(_hub!.Services.Get("dialogue"), _hub.CheckThread, error => _hub.ReportSubscriberFailure("dialogue", error), _storyCharacters ??= new StoryCharacterService(_hub));
        _hub.SetCapability("dialogue", false, "Dialogue bindings are initializing.");
        try
        {
            var type = Assembly.Load("Assembly-CSharp").GetType("Behaviour.Dialogues.DialogueManager", true)!;
            Patches.DialoguePatches.Install(_dialogueService, type, error => _hub.ReportSubscriberFailure("dialogue", error));
            _dialogueHarmony = new Harmony(ModApi.PluginId + ".dialogue");
            _dialogueHarmony.Patch(type.GetMethod("ShowDialogueLine", BindingFlags.Instance | BindingFlags.NonPublic)!,
                postfix: new HarmonyMethod(typeof(Patches.DialoguePatches), nameof(Patches.DialoguePatches.Line)));
            _dialogueHarmony.Patch(type.GetMethod("CloseDialogue", BindingFlags.Instance | BindingFlags.Public)!,
                postfix: new HarmonyMethod(typeof(Patches.DialoguePatches), nameof(Patches.DialoguePatches.Close)));
            SceneManager.sceneUnloaded += DialogueSceneUnloaded;
            _hub.Changed += DialogueLifecycle;
            _hub.SetCapability("dialogue", true, "Conversation-manager line and closure observations.");
        }
        catch (Exception error) { _dialogueHarmony?.UnpatchSelf(); Patches.DialoguePatches.Clear(); _hub.SetCapability("dialogue", false, error.Message); }
    }
    private void DialogueSceneUnloaded(Scene scene)
    { if (scene.name == "UI - Dialogue") _dialogueService?.Close(); }
    private void DialogueLifecycle(LifecycleEvent change)
    {
        if (change.Kind == LifecycleEventKind.SessionStarting || change.Kind == LifecycleEventKind.SessionInvalidated || change.Kind == LifecycleEventKind.SessionStartFailed)
            _dialogueService?.Reset(change.Kind == LifecycleEventKind.SessionStarting ? change.Session?.Id : null);
    }
    private void StopDialogue()
    {
        SceneManager.sceneUnloaded -= DialogueSceneUnloaded;
        if (_hub != null) _hub.Changed -= DialogueLifecycle;
        _dialogueService?.Dispose(); Patches.DialoguePatches.Clear(); _dialogueHarmony?.UnpatchSelf();
    }
}
