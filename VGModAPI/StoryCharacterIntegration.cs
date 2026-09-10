using System;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

public sealed partial class Plugin
{
    private StoryCharacterService? _storyCharacters;
    private Harmony? _storyCharacterHarmony;

    private void InstallStoryCharacters(Assembly assembly)
    {
        _storyCharacters ??= new StoryCharacterService(_hub!);
        try
        {
            var methods = StoryCharacterBindings.Validate(assembly);
            // The game keeps every NPC portrait under one resource set; loading by art name is the
            // primitive its own factories use, so every shipped portrait stays expressible.
            StoryCharacterPatches.Runtime = new StoryCharacterRuntime(assembly, _storyCharacters,
                art => UnityEngine.Resources.Load<UnityEngine.Sprite>("Sprites/NPC/" + art),
                error => Logger.LogError(error));
            _storyCharacterHarmony = new Harmony(ModApi.PluginId + ".story-characters");
            _storyCharacterHarmony.Patch(methods["characterLookup"],
                prefix: new HarmonyMethod(typeof(StoryCharacterPatches.Lookup), "Prefix"),
                postfix: new HarmonyMethod(typeof(StoryCharacterPatches.Lookup), "Postfix"));
            _storyCharacters.SetAvailable(true);
        }
        catch (Exception error) { TeardownStoryCharacters(); Logger.LogError(error); }
    }

    private void TeardownStoryCharacters()
    {
        StoryCharacterPatches.Runtime = null;
        try { _storyCharacterHarmony?.UnpatchSelf(); }
        catch (Exception error) { Logger.LogError(error); }
        _storyCharacterHarmony = null;
        _storyCharacters?.SetAvailable(false);
    }
}
