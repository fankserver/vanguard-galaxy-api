using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core.Integration;

internal interface IWorldFactoryCaptureHost
{
    object? BeginFactory(object value);
    void CompleteFactory(object token, object result);
}

internal sealed partial class WorldLoadHookHost : IWorldFactoryCaptureHost
{
    private sealed class FactoryCapture
    {
        internal readonly WorldPreparedLoad Load;
        internal readonly WorldConstructionNode Node;
        internal FactoryCapture(WorldPreparedLoad load, WorldConstructionNode node) { Load = load; Node = node; }
    }
    private ConditionalWeakTable<object, FactoryCapture> _factoryTokens = new();
    private HashSet<string> _factoryIds = new(StringComparer.Ordinal);
    private Dictionary<string, object> _constructed = new(StringComparer.Ordinal);
    private bool _factoryRejected;
    private void ResetFactories() { _factoryRejected = false; _factoryTokens = new(); _factoryIds = new(StringComparer.Ordinal); _constructed = new(StringComparer.Ordinal); }

    public object? BeginFactory(object value)
    {
        var node = AdmitFactory(value);
        if (node == null) return null;
        var load = PreparedFor(_sessionId);
        if (load == null || _hub.CurrentSession?.Phase != SessionPhase.Starting || _recallRejected)
            throw new InvalidDataException("World constructor has no current prepared load.");
        _json.RequireFactory(_gate, load.Session, value, load.ProviderRevision);
        if (!_factoryIds.Add(node.Identity.NativeId)) { _factoryRejected = true; _gate.Invalidate(); throw new InvalidDataException("Owned world constructor already attempted."); }
        var token = new object(); _factoryTokens.Add(token, new FactoryCapture(load, node)); return token;
    }
    public void CompleteFactory(object token, object result)
    {
        _hub.CheckThread();
        if (!_factoryTokens.TryGetValue(token, out var capture)) throw new InvalidDataException("Unknown world constructor completion.");
        _factoryTokens.Remove(token);
        if (!ReferenceEquals(PreparedFor(capture.Load.Session), capture.Load) || _hub.CurrentSession?.Phase != SessionPhase.Starting || _recallRejected)
            throw new InvalidDataException("Stale world constructor completion.");
        // Revalidate the exact admitted JSON after native construction before retaining its result.
        _gate.RequireFactory(capture.Load.Session, capture.Node.Json, capture.Node.Identity.NativeId,
            WorldJsonInspection.Digest(capture.Node.Json), capture.Load.ProviderRevision);
        var type = result?.GetType();
        var nativeAssembly = _file.DeclaringType!.Assembly;
        if (type != nativeAssembly.GetType("Source.Galaxy.POI.Combat", true)) throw new InvalidDataException("Unexpected world constructor result type.");
        var guid = nativeAssembly.GetType("Source.Galaxy.MapElement", true)!.GetField("<guid>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        if ((string?)guid?.GetValue(result) != capture.Node.Identity.NativeId) throw new InvalidDataException("World constructor result identity mismatch.");
        _constructed.Add(capture.Node.Identity.NativeId, result!);
    }
    internal bool ConstructedBy(WorldPreparedLoad load, WorldSnapshotInstance instance)
    {
        _hub.CheckThread();
        return ReferenceEquals(PreparedFor(load.Session), load) && _constructed.TryGetValue(instance.Identity.NativeId, out var native) &&
            ReferenceEquals(native, instance.Native);
    }
}
