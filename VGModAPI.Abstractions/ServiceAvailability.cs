using System;

namespace VGModAPI;

public enum ServiceUnavailableReason
{
    None,
    Disabled,
    UnsupportedGame,
    BindingFailed,
    DependencyUnavailable,
    ObserverFault,
    ApiStopped
}

/// <summary>Immutable service health, not session readiness, action permission or qualification.</summary>
public sealed class ServiceAvailability : IEquatable<ServiceAvailability>
{
    public static ServiceAvailability Available { get; } = new(ServiceUnavailableReason.None);
    public bool IsAvailable => Reason == ServiceUnavailableReason.None;
    public ServiceUnavailableReason Reason { get; }
    /// <summary>For diagnostics only. Branch on Reason, never this text.</summary>
    public string Detail { get; }

    public ServiceAvailability(ServiceUnavailableReason reason, string detail = "")
    {
        if (!Enum.IsDefined(typeof(ServiceUnavailableReason), reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        Reason = reason;
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }

    public bool Equals(ServiceAvailability? other) => other != null && Reason == other.Reason && Detail == other.Detail;
    public override bool Equals(object? obj) => obj is ServiceAvailability other && Equals(other);
    public override int GetHashCode() => unchecked(((int)Reason * 397) ^ StringComparer.Ordinal.GetHashCode(Detail));
}

/// <summary>
/// Main-thread-only status. Subscribe, then read Availability; subscribing does not replay.
/// Notifications describe changes, never grant permission to mutate during callback delivery.
/// </summary>
public interface IServiceStatus
{
    ServiceAvailability Availability { get; }
    event Action<ServiceAvailability>? AvailabilityChanged;
}
