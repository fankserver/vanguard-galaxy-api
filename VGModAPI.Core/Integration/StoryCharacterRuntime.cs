using System;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>
/// Builds and augments native characters at registry-lookup time. Declarations are re-consulted on
/// every lookup and on every click, so disposal takes effect without touching already-built
/// instances, and any fault fails open to vanilla with one report.
/// </summary>
internal sealed class StoryCharacterRuntime
{
    private readonly StoryCharacterService _service;
    private readonly Action<Exception> _report;
    private readonly Type _character, _dialogue;
    private readonly ConstructorInfo _characterCtor, _lineCtor;
    private readonly FieldInfo _name, _description, _portrait, _createDialogue, _missionIds, _captain, _shipAi, _lines, _onComplete;
    private readonly MethodInfo _lookup;
    private readonly Type _createDialogueType, _linesType, _lineListType;
    private bool _reported;

    internal StoryCharacterRuntime(Assembly assembly, StoryCharacterService service, Action<Exception> report)
    {
        _service = service; _report = report;
        _character = assembly.GetType("Source.Dialogues.Character", true)!;
        _dialogue = assembly.GetType("Source.Dialogues.Dialogue", true)!;
        var line = assembly.GetType("Source.Dialogues.DialogueLine", true)!;
        var characters = assembly.GetType("Source.Dialogues.Characters", true)!;
        _characterCtor = _character.GetConstructor(new[] { typeof(string) })
            ?? throw new MissingMethodException(_character.FullName, ".ctor(string)");
        _lineCtor = line.GetConstructor(new[] { _character, typeof(string) })
            ?? throw new MissingMethodException(line.FullName, ".ctor(Character, string)");
        _name = Field(_character, "name"); _description = Field(_character, "description");
        _portrait = Field(_character, "portretSprite");
        _createDialogue = Field(_character, "createDialogue"); _missionIds = Field(_character, "missionIds");
        _captain = Field(characters, "captain"); _shipAi = Field(characters, "shipAi");
        _lines = Field(_dialogue, "dialogues"); _onComplete = Field(_dialogue, "onComplete");
        _lookup = characters.GetMethod("GetCharacter", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException(characters.FullName, "GetCharacter");
        if (_lookup.ReturnType != _character) throw new MissingMethodException(characters.FullName, "GetCharacter");
        _createDialogueType = _createDialogue.FieldType;
        _linesType = _lines.FieldType;
        _lineListType = typeof(System.Collections.Generic.List<>).MakeGenericType(line);
        if (_missionIds.FieldType != typeof(System.Collections.Generic.List<string>) ||
            _createDialogueType != typeof(Func<,>).MakeGenericType(_character, _dialogue) ||
            _linesType != typeof(Func<>).MakeGenericType(_lineListType) ||
            _onComplete.FieldType != typeof(Action))
            throw new MissingFieldException(_character.FullName, "character content shapes");
    }
    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        ?? throw new MissingFieldException(type.FullName, name);

    /// <summary>Native character for an owned lookup name, or null for every other name.</summary>
    internal object? ResolveOwned(string? name)
    {
        try
        {
            if (_service.IntroductionFor(name) is not { } introduction) return null;
            var character = _characterCtor.Invoke(new object[] { introduction.Definition.Name });
            _description.SetValue(character, introduction.Definition.Description);
            if (introduction.Definition.PortraitOf is { } portraitOf &&
                _lookup.Invoke(null, new object[] { portraitOf }) is { } source)
                _portrait.SetValue(character, _portrait.GetValue(source));
            var lookupName = introduction.LookupName;
            _createDialogue.SetValue(character, CreateDialogueDelegate(self =>
                BuildConversation(self, _service.IntroductionFor(lookupName)?.Conversation)));
            Highlight(character, introduction.MissionHighlights);
            return character;
        }
        catch (Exception error) { ReportOnce(error); return null; }
    }

    /// <summary>Consults live extensions first on each click; null falls back to the character's own dialogue.</summary>
    internal void ApplyExtensions(string? name, object? character)
    {
        try
        {
            if (name == null || character == null || !_character.IsInstanceOfType(character)) return;
            var extensions = _service.ExtensionsFor(name);
            if (extensions.Length == 0) return;
            var original = _createDialogue.GetValue(character) as Delegate;
            _createDialogue.SetValue(character, CreateDialogueDelegate(self =>
            {
                foreach (var extension in _service.ExtensionsFor(name))
                {
                    // One owner's fault must not silence another owner or the character itself.
                    object? built;
                    try { built = BuildConversation(self, extension.Conversation); }
                    catch (Exception error) { ReportOnce(error); continue; }
                    if (built != null) return built;
                }
                return original?.DynamicInvoke(self);
            }));
            foreach (var extension in extensions) Highlight(character, extension.MissionHighlights);
        }
        catch (Exception error) { ReportOnce(error); }
    }

    private void Highlight(object character, string[] missionIds)
    {
        var list = (IList)_missionIds.GetValue(character)!;
        foreach (var missionId in missionIds)
            if (!list.Contains(missionId)) list.Add(missionId);
    }

    private object CreateDialogueDelegate(Func<object, object?> bridge)
    {
        Func<object, object?> guarded = self =>
        {
            try { return bridge(self); }
            catch (Exception error) { ReportOnce(error); return null; }
        };
        var self = Expression.Parameter(_character, "character");
        var invoke = Expression.Call(Expression.Constant(guarded), guarded.GetType().GetMethod("Invoke")!,
            Expression.Convert(self, typeof(object)));
        return Expression.Lambda(_createDialogueType, Expression.Convert(invoke, _dialogue), self).Compile();
    }

    private object? BuildConversation(object self, Func<CharacterConversation?>? conversation)
    {
        var content = conversation?.Invoke();
        if (content == null) return null;
        var lines = (IList)Activator.CreateInstance(_lineListType)!;
        foreach (var line in content.Lines)
            lines.Add(_lineCtor.Invoke(new[] { Speaker(self, line.Speaker), (object)line.Text }));
        var dialogue = Activator.CreateInstance(_dialogue)!;
        _lines.SetValue(dialogue, Expression.Lambda(_linesType, Expression.Constant(lines, _lineListType)).Compile());
        var completed = content.Completed;
        if (completed != null)
            _onComplete.SetValue(dialogue, (Action)(() =>
            {
                try { completed(); }
                catch (Exception error) { ReportOnce(error); }
            }));
        return dialogue;
    }

    private object Speaker(object self, string? speaker)
    {
        if (speaker == null) return self;
        var known = speaker switch
        {
            CharacterSpeakers.Captain => _captain.GetValue(null),
            CharacterSpeakers.ShipAi => _shipAi.GetValue(null),
            _ => _lookup.Invoke(null, new object[] { speaker })
        };
        if (known != null) return known;
        var display = speaker == CharacterSpeakers.Captain ? "Captain" : speaker == CharacterSpeakers.ShipAi ? "ECHO" : speaker;
        return _characterCtor.Invoke(new object[] { display });
    }

    private void ReportOnce(Exception error)
    {
        if (_reported) return;
        _reported = true;
        try { _report(error); } catch { }
    }
}
