using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal static class StoryCharacterBindings
{
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        ("Source.Dialogues.Character", "name", "System.String", false, true),
        ("Source.Dialogues.Character", "description", "System.String", false, true),
        ("Source.Dialogues.Character", "portretSprite", "UnityEngine.Sprite", false, true),
        ("Source.Dialogues.Character", "createDialogue", "System.Func`2<Source.Dialogues.Character,Source.Dialogues.Dialogue>", false, true),
        ("Source.Dialogues.Character", "missionIds", "System.Collections.Generic.List`1<System.String>", false, true),
        ("Source.Dialogues.Characters", "captain", "Source.Dialogues.Character", true, true),
        ("Source.Dialogues.Characters", "shipAi", "Source.Dialogues.Character", true, true),
        ("Source.Dialogues.Dialogue", "dialogues", "System.Func`1<System.Collections.Generic.List`1<Source.Dialogues.DialogueLine>>", false, true),
        ("Source.Dialogues.Dialogue", "onComplete", "System.Action", false, true)
    };
    internal static readonly MethodBinding[] Methods =
    {
        new("characterLookup", "Source.Dialogues.Characters", "GetCharacter", true, "Source.Dialogues.Character", "System.String")
    };
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        RecipeCatalogBindings.Validate(assembly, Members);
        return new GameBindings(assembly).Resolve(Methods);
    }
}
