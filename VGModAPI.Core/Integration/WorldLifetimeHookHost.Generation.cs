using System;
using System.IO;

namespace VGModAPI.Core.Integration;

internal interface IWorldGenerationHost
{
    WorldGenerationAttempts.Scope BeginGeneration(object poi);
    Exception? EndGeneration(WorldGenerationAttempts.Scope scope, Exception? error);
    void ConsumeBuilder();
}
internal sealed partial class WorldLifetimeHookHost : IWorldGenerationHost
{
    private readonly WorldGenerationAttempts _generation = new(1024);
    private readonly Action<Guid>? _generationFailure;
    public WorldGenerationAttempts.Scope BeginGeneration(object poi)
    {
        _hub.CheckThread();
        if (AllowRemoval(poi)) return _generation.Begin(null);
        var session = _hub.CurrentSession?.Id; var identity = Identity(poi);
        try
        {
            return _generation.Begin(() => !_disposed && session == _hub.CurrentSession?.Id && session == _session &&
                Identity(poi) == identity && AllowUse(poi) && session == _hub.CurrentSession?.Id && Identity(poi) == identity);
        }
        catch (Exception error) { RefuseFailedGeneration(error); throw; }
    }
    public Exception? EndGeneration(WorldGenerationAttempts.Scope scope, Exception? error)
    {
        _hub.CheckThread();
        var result = scope.Finish(error);
        try { RefuseFailedGeneration(); }
        catch (Exception cleanup) { return result == null ? cleanup : new AggregateException(result, cleanup); }
        return result;
    }
    public void ConsumeBuilder()
    {
        _hub.CheckThread();
        try { _generation.Consume(); }
        catch (Exception error) { RefuseFailedGeneration(error); throw; }
    }
    private void RefuseFailedGeneration(Exception? original = null)
    {
        if (!_generation.Failed) return;
        _guard.Invalidate();
        try { _generationFailure?.Invoke(_session); }
        catch (Exception cleanup) when (original != null) { throw new AggregateException(original, cleanup); }
    }
}
