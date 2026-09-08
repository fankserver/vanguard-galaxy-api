using System;

namespace VGModAPI.Qualification;

internal sealed class ProbeHeartbeat
{
    private readonly int _startFrame;
    private double _lastTime;
    internal ProbeHeartbeat(int frame, double time) { _startFrame = frame; _lastTime = time; }
    internal void Tick(double time)
    {
        if (time < _lastTime || time - _lastTime > 2) throw new InvalidOperationException("Wire probe blocked the Unity heartbeat.");
        _lastTime = time;
    }
    internal void Complete(int frame, double time)
    {
        Tick(time);
        if (frame < _startFrame + 3) throw new InvalidOperationException("Wire phase has no independent frame progress.");
    }
}
