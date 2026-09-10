using System;
using System.Collections.Generic;

namespace Source.Dialogues
{
    public class Character
    {
        public string name;
        public string? description;
        public UnityEngine.Sprite? portretSprite;
        public Func<Character, Dialogue?>? createDialogue;
        public List<string> missionIds = new();
        public Character(string name) => this.name = name;
    }
    public class Dialogue
    {
        public Func<List<DialogueLine>>? dialogues;
        public Action? onComplete;
    }
    public class DialogueLine
    {
        public readonly Character character;
        public readonly string text;
        public DialogueLine(Character character, string text) { this.character = character; this.text = text; }
    }
    public static class Characters
    {
        public static Character? captain;
        public static Character? shipAi;
        public static Func<string, Character?>? TestRegistry;
        public static Character? GetCharacter(string name) => TestRegistry?.Invoke(name);
    }
}
