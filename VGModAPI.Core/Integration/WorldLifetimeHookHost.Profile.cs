using System;
using System.IO;

namespace VGModAPI.Core.Integration;

internal interface IWorldProfileHost { void RequireContentMutation(object poi); }
internal sealed partial class WorldLifetimeHookHost : IWorldProfileHost
{
    // Fixed internal inspection only: this delegate must not invoke callbacks or mutate native state.
    private readonly Action<object>? _stateProfile;
    private bool CheckStateProfile(object poi)
    {
        if (_stateProfile == null || AllowRemoval(poi)) return true;
        try { _stateProfile(poi); return true; }
        catch (Exception error) { RefuseProfile(poi, error); throw; }
    }
    public void RequireContentMutation(object poi)
    {
        _hub.CheckThread();
        if (_stateProfile == null || AllowRemoval(poi)) return;
        var error = new InvalidDataException("Content mutation is outside the supported empty Combat profile.");
        RefuseProfile(poi, error); throw error;
    }
    private void RefuseProfile(object poi, Exception original)
    {
        var session = _session;
        if (session != _hub.CurrentSession?.Id || !_guard.CheckAmbient(session, poi, Identity(poi))) return;
        _guard.Invalidate();
        try { _generationFailure?.Invoke(session); }
        catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
    }
}
