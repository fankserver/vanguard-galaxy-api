using System;
using UnityEngine;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class DungeonReturnPodInstance : IDungeonReturnInstance
{
    private readonly GameObject _object;
    private bool _activated, _disposed;
    internal object Pod { get; }
    internal object Data { get; }
    internal object Operation { get; }
    internal DungeonReturnPodInstance((GameObject Object, object Pod, object Data, object Operation) instance)
    { _object = instance.Object; Pod = instance.Pod; Data = instance.Data; Operation = instance.Operation; }
    public bool Alive => !_disposed && _object;
    public void Activate()
    {
        if (!Alive || _activated) throw new InvalidOperationException("Return instance is unavailable or already active.");
        _activated = true; _object.SetActive(true);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_object) { _object.SetActive(false); UnityEngine.Object.Destroy(_object); }
    }
}
