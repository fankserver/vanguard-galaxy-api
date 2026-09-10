using System;
using System.Collections.Generic;

namespace VGModAPI.Core.Integration;

internal sealed class BarPortraitResolver
{
    private readonly Func<string, object?> _named, _character;
    private readonly Func<object, bool> _isSprite;
    private readonly Action<Exception> _report;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    internal BarPortraitResolver(Func<string, object?> named, Func<string, object?> character,
        Func<object, bool> isSprite, Action<Exception> report)
    {
        _named = named ?? throw new ArgumentNullException(nameof(named));
        _character = character ?? throw new ArgumentNullException(nameof(character));
        _isSprite = isSprite ?? throw new ArgumentNullException(nameof(isSprite));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }
    internal object? Resolve(CharacterPortrait portrait)
    {
        var identity = portrait.PortraitName is { } art ? "NPC portrait '" + art + "'" : "portrait of character '" + portrait.RegistryName + "'";
        try
        {
            var result = portrait.PortraitName is { } name ? _named(name) : _character(portrait.RegistryName!);
            if (result != null && _isSprite(result)) return result;
        }
        catch (Exception) { }
        if (_reported.Add(identity))
            try { _report(new InvalidOperationException("The game could not resolve " + identity + "; the bar contact has no portrait.")); }
            catch (Exception) { }
        // Do not cache a missing sprite: native catalogs can become available after this build.
        return null;
    }
}
