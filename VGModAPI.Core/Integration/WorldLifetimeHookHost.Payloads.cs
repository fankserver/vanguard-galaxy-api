using System;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

internal interface IWorldPayloadLifetimeHost
{
    void RequirePayload(object payload);
}

internal sealed partial class WorldLifetimeHookHost : IWorldPayloadLifetimeHost
{
    public void RequirePayload(object payload)
    {
        _hub.CheckThread();
        var type = _guid.DeclaringType!.Assembly.GetType("Source.Galaxy.MapTriggeredPayload", true)!;
        var parent = type.GetField("parent", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("MapTriggeredPayload.parent");
        if (!parent.IsInitOnly || parent.FieldType != _localTarget.FieldType || !type.IsInstanceOfType(payload))
            throw new InvalidDataException("Unsupported triggered payload ownership.");
        var poi = parent.GetValue(payload) ?? throw new InvalidDataException("Triggered payload has no parent.");
        if (!AllowUse(poi)) throw new InvalidDataException("Triggered world payload is quarantined.");
        if (!AllowRemoval(poi))
        {
            var player = _player.GetValue(null);
            if (player == null || !ReferenceEquals(_playerPoi.GetValue(player), poi))
                throw new InvalidDataException("Owned triggered payload is not in the current player POI.");
        }
    }
}
