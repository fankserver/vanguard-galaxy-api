using System;

namespace VGModAPI.Core.Integration;

/// <summary>Registers complete world teardown before its save-data coordinator, including deferred root shutdown.</summary>
internal static class WorldShutdownRegistration
{
    internal static void Register(ServiceStatusRegistry services, Action stopWorld, IDisposable persistence)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (stopWorld == null) throw new ArgumentNullException(nameof(stopWorld));
        if (persistence == null) throw new ArgumentNullException(nameof(persistence));
        services.AfterStopped(stopWorld);
        services.AfterStopped(persistence.Dispose);
    }
}
