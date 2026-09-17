using System;

namespace VGModAPI.Core;

internal sealed class SkillTreeService : ISkillTreeService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private Func<CommanderSpecialization, SkillTree?>? _read;
    private bool _disposed;
    internal SkillTreeService(LifecycleHub hub) { _hub = hub; _status = hub.Services.Get("skill-trees"); }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    internal void Bind(Func<CommanderSpecialization, SkillTree?>? read)
    {
        _hub.CheckThread(); if (_disposed) return;
        _read = read;
        if (read != null) _hub.SetAvailable("skill-trees", "Commander skill trees bound.");
        else _hub.SetUnavailable("skill-trees", ServiceUnavailableReason.BindingFailed, "Skill trees unavailable.");
    }
    public SkillTree? Get(CommanderSpecialization specialization)
    {
        _hub.CheckThread();
        if (!Enum.IsDefined(typeof(CommanderSpecialization), specialization)) throw new ArgumentOutOfRangeException(nameof(specialization));
        if (_disposed || !Availability.IsAvailable || _hub.Services.IsStopping) return null;
        try { return _read?.Invoke(specialization); }
        catch (Exception error) { _hub.ReportSubscriberFailure("skill-trees", error); return null; }
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true; _read = null;
        _hub.SetUnavailable("skill-trees", ServiceUnavailableReason.ApiStopped, "Skill trees stopped.");
    }
}
