using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>
/// Author-keyed replacement for declaration surfaces. A key is scoped to the declaring assembly
/// (and an optional extra scope object), so re-declaring the same key replaces only the author's
/// own previous declaration; other authors' declarations are untouched. Disposing a superseded
/// handle is inert and can never revoke the replacement.
/// </summary>
internal sealed class KeyedDeclarations
{
    private readonly Dictionary<(object Scope, string Key), IDisposable> _current = new();

    internal static string? Check(string? key, string parameter)
    {
        if (key == null) return null;
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new ArgumentException("A declaration key must be nonempty and at most 128 characters.", parameter);
        foreach (char character in key)
            if (char.IsControl(character)) throw new ArgumentException("Control characters are not supported.", parameter);
        return key;
    }

    /// <summary>Replaces the author's previous declaration under the key, disposing it first.</summary>
    internal void Replace(object scope, string? key, IDisposable declaration)
    {
        if (key == null) return;
        if (_current.TryGetValue((scope, key), out var previous))
            try { previous.Dispose(); } catch { }
        _current[(scope, key)] = declaration;
    }

    /// <summary>Forgets a handle only while it is still the key's current declaration.</summary>
    internal void Forget(object scope, string? key, IDisposable declaration)
    {
        if (key == null) return;
        if (_current.TryGetValue((scope, key), out var current) && ReferenceEquals(current, declaration))
            _current.Remove((scope, key));
    }

    internal void Clear() => _current.Clear();
}
