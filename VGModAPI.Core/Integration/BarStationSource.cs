using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core.Integration;

/// <summary>Callback-free current-station reads for the final native commit boundary.</summary>
internal sealed class BarStationSource
{
    private readonly FieldInfo _player, _poi;
    private readonly Type _stationType;

    internal BarStationSource(Type playerType, Type stationType)
    {
        _player = playerType.GetField("current", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingFieldException("GamePlayer.current must be a field, not a callback or property.");
        _poi = playerType.GetField("currentPointOfInterest", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("GamePlayer.currentPointOfInterest");
        if (_player.FieldType != playerType || !_poi.FieldType.IsAssignableFrom(stationType))
            throw new InvalidOperationException("Unsupported current-station field chain.");
        _stationType = stationType;
        // Any type initializer runs during binding, never during a commit-time field read.
        RuntimeHelpers.RunClassConstructor(playerType.TypeHandle);
    }

    internal object? Read()
    {
        var player = _player.GetValue(null);
        if (player == null) return null;
        var poi = _poi.GetValue(player);
        return poi != null && _stationType.IsInstanceOfType(poi) ? poi : null;
    }
}
