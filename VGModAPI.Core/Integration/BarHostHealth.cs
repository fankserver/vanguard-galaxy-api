namespace VGModAPI.Core.Integration;

/// <summary>Monotonic, callback-free admission shared by a host and its native world.</summary>
internal sealed class BarHostHealth
{
    internal bool IsHealthy { get; private set; } = true;
    internal void Fault() => IsHealthy = false;
}
