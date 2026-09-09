using System;
using System.Collections;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Patches;

internal static class DialoguePatches
{
    private static DialogueService? _service;
    private static FieldInfo? _lines, _index;
    private static Action<Exception>? _report;
    internal static void Install(DialogueService service, Type manager, Action<Exception> report)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        _lines = manager.GetField("dialogue", flags) ?? throw new MissingFieldException(manager.FullName, "dialogue");
        _index = manager.GetField("currentLineIndex", flags) ?? throw new MissingFieldException(manager.FullName, "currentLineIndex");
        _report = report; _service = service;
    }
    internal static void Clear() { _service = null; _lines = _index = null; _report = null; }
    public static void Line(object __instance)
    {
        try
        {
            if (_service == null || _lines?.GetValue(__instance) is not IList lines || _index?.GetValue(__instance) is not int index || index < 0 || index >= lines.Count) return;
            var line = lines[index]; if (line == null) return;
            var character = Value(line, "character");
            _service.Present(lines, index, character == null ? "" : Value(character, "name") as string ?? "", Value(line, "text") as string ?? "");
        }
        catch (Exception error) { try { _report?.Invoke(error); } catch { } }
    }
    public static void Close()
    { try { _service?.Close(); } catch (Exception error) { try { _report?.Invoke(error); } catch { } } }
    private static object? Value(object value, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = value.GetType();
        var field = type.GetField(name, flags);
        return field != null ? field.GetValue(value) : type.GetProperty(name, flags)?.GetValue(value);
    }
}
