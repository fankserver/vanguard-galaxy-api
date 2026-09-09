using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

internal enum HudIconState { Pending, Resolved, LabelFallback }

/// <summary>Resolution progresses independently of content revisions and layout rebuilds.</summary>
internal sealed class HudIconResolver<T> where T : class
{
    internal readonly struct Visual
    {
        internal T? Icon { get; }
        internal HudIconState State { get; }
        internal Visual(T? icon, HudIconState state) { Icon = icon; State = state; }
    }
    private sealed class Entry
    {
        internal readonly double Started;
        internal double NextAttempt;
        internal T? Icon;
        internal bool Reported;
        internal Entry(double now) { Started = now; NextAttempt = now; }
    }
    private readonly Func<HudIcon, T?> _resolve;
    private readonly Func<T, bool> _alive;
    private readonly Action<Exception> _report;
    private readonly Dictionary<HudIcon, Entry> _entries = new();
    internal HudIconResolver(Func<HudIcon, T?> resolve, Func<T, bool> alive, Action<Exception> report)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _alive = alive ?? throw new ArgumentNullException(nameof(alive));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }
    internal Visual Read(HudIcon icon, double now)
    {
        if (!Enum.IsDefined(typeof(HudIcon), icon)) throw new ArgumentOutOfRangeException(nameof(icon));
        if (double.IsNaN(now) || double.IsInfinity(now) || now < 0) throw new ArgumentOutOfRangeException(nameof(now));
        if (!_entries.TryGetValue(icon, out var entry)) _entries.Add(icon, entry = new Entry(now));
        try
        {
            if (entry.Icon != null && _alive(entry.Icon)) return new Visual(entry.Icon, HudIconState.Resolved);
            entry.Icon = null;
            if (now >= entry.NextAttempt)
            {
                entry.NextAttempt = now + 1;
                var resolved = _resolve(icon);
                if (resolved != null && _alive(resolved))
                { entry.Icon = resolved; return new Visual(resolved, HudIconState.Resolved); }
            }
        }
        catch (Exception error)
        {
            entry.Icon = null; entry.NextAttempt = now + 1;
            if (!entry.Reported)
            {
                entry.Reported = true;
                try { _report(error); } catch { }
            }
        }
        return new Visual(null, now - entry.Started >= 2 ? HudIconState.LabelFallback : HudIconState.Pending);
    }
    internal void Clear() => _entries.Clear();
}

/// <summary>Private mappings for the inspected game build, not consumer asset lookup keys.</summary>
internal static class HudIconSprites
{
    internal static bool Matches(HudIcon icon, string name, float x, float y) => icon switch
    {
        HudIcon.Storage => name == "SkillIcons1_31" && x == 463 && y == 793,
        HudIcon.Refinery => name == "Refinery",
        _ => false
    };
}
