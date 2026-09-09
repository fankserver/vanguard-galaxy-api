using System;
using System.IO;
using System.Collections;
using System.Reflection;

namespace VGModAPI.Core.Integration;

internal interface IWorldGenerationHost
{
    WorldGenerationAttempts.Scope BeginGeneration(object poi, Func<bool>? stable = null);
    WorldGenerationAttempts.Scope BeginSalvageSlot(object poi, int slot);
    void ValidateGeneration();
    Exception? EndGeneration(WorldGenerationAttempts.Scope scope, Exception? error);
    void ConsumeBuilder();
}
internal sealed partial class WorldLifetimeHookHost : IWorldGenerationHost
{
    private readonly WorldGenerationAttempts _generation = new(1024);
    private readonly Action<Guid>? _generationFailure;
    public WorldGenerationAttempts.Scope BeginSalvageSlot(object poi, int slot)
    {
        _hub.CheckThread();
        if (AllowRemoval(poi)) return BeginGeneration(poi);
        var field = poi.GetType().GetField("salvageDescriptors", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("MapPointOfInterest.salvageDescriptors");
        var list = field.GetValue(poi) as IList;
        if (list == null || list.GetType() != field.FieldType || slot < 0 || slot >= list.Count || list.Count > 1024) throw new InvalidDataException("Invalid owned salvage slot.");
        int count = list.Count; var descriptor = list[slot];
        if (descriptor == null) throw new InvalidDataException("Missing owned salvage descriptor.");
        return BeginGeneration(poi, () => ReferenceEquals(field.GetValue(poi), list) && list.Count == count && ReferenceEquals(list[slot], descriptor));
    }
    public void ValidateGeneration()
    {
        _hub.CheckThread();
        try { _generation.ValidateCurrent(); }
        catch (Exception error) { RefuseFailedGeneration(error); throw; }
    }
    public WorldGenerationAttempts.Scope BeginGeneration(object poi, Func<bool>? stable = null)
    {
        _hub.CheckThread();
        if (AllowRemoval(poi)) return _generation.Begin(null);
        var session = _hub.CurrentSession?.Id; var identity = Identity(poi); var player = _player.GetValue(null);
        try
        {
            return _generation.Begin(() => !_disposed && session == _hub.CurrentSession?.Id && session == _session && AllowUse(poi),
                () => !_disposed && session == _hub.CurrentSession?.Id && session == _session &&
                    ReferenceEquals(player, _player.GetValue(null)) && Identity(poi) == identity && StillAllowed(poi) && (stable?.Invoke() ?? true));
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
