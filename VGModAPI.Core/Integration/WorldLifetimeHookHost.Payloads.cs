using System;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

internal interface IWorldPayloadLifetimeHost
{
    void RequirePayload(object payload);
    void RequirePayloadAttachment(object poi, object payload);
}

internal sealed partial class WorldLifetimeHookHost : IWorldPayloadLifetimeHost
{
    private object PayloadParent(object payload)
    {
        var type = _guid.DeclaringType!.Assembly.GetType("Source.Galaxy.MapTriggeredPayload", true)!;
        var parent = type.GetField("parent", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("MapTriggeredPayload.parent");
        if (!parent.IsInitOnly || parent.FieldType != _localTarget.FieldType || !type.IsInstanceOfType(payload))
            throw new InvalidDataException("Unsupported triggered payload ownership.");
        return parent.GetValue(payload) ?? throw new InvalidDataException("Triggered payload has no parent.");
    }
    public void RequirePayloadAttachment(object poi, object payload)
    {
        _hub.CheckThread();
        var parent = PayloadParent(payload);
        var poiId = Identity(poi); var parentId = Identity(parent);
        if (!AllowUse(poi) || !AllowUse(parent) || !StillAllowed(poi) || !StillAllowed(parent) ||
            Identity(poi) != poiId || Identity(parent) != parentId || !ReferenceEquals(parent, PayloadParent(payload)))
            throw new InvalidDataException("Triggered payload attachment changed or is quarantined.");
        if ((!AllowRemoval(poi) || !AllowRemoval(parent)) && !ReferenceEquals(poi, parent))
            throw new InvalidDataException("Owned triggered payload cannot be attached to another POI.");
    }
    public void RequirePayload(object payload)
    {
        _hub.CheckThread();
        var poi = PayloadParent(payload);
        var player = _player.GetValue(null);
        if (!AllowUse(poi) || !ReferenceEquals(poi, PayloadParent(payload)) || !ReferenceEquals(player, _player.GetValue(null)))
            throw new InvalidDataException("Triggered world payload changed or is quarantined.");
        if (!AllowRemoval(poi))
        {
            if (player == null || !ReferenceEquals(_playerPoi.GetValue(player), poi))
                throw new InvalidDataException("Owned triggered payload is not in the current player POI.");
        }
    }
}
