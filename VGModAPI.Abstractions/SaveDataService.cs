using System;

namespace VGModAPI;

public enum SaveDataStateKind { Inactive, Restoring, Ready, Blocked, Disposed }
public enum SaveDataBlockReason
{
    None, LoadRefused, SchemaUnavailable, RestoreFailed, CaptureFailed,
    PublicationFailed, ProviderRemoved, LimitExceeded, ServiceUnavailable
}

/// <summary>Durable provider state for one tracked session; does not encode transient callback/save restrictions.</summary>
public sealed class SaveDataState
{
    public SaveDataStateKind Kind { get; }
    public Guid? SessionId { get; }
    public SaveDataBlockReason Reason { get; }
    public string Detail { get; }

    public SaveDataState(SaveDataStateKind kind, Guid? sessionId = null,
        SaveDataBlockReason reason = SaveDataBlockReason.None, string detail = "")
    {
        if (!Enum.IsDefined(typeof(SaveDataStateKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(typeof(SaveDataBlockReason), reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (sessionId == Guid.Empty) throw new ArgumentException("A session identity cannot be empty.", nameof(sessionId));
        if ((kind is SaveDataStateKind.Ready or SaveDataStateKind.Restoring) && sessionId == null)
            throw new ArgumentException("Restoration requires a tracked session.", nameof(sessionId));
        if ((kind is SaveDataStateKind.Inactive or SaveDataStateKind.Disposed) && sessionId != null)
            throw new ArgumentException("Inactive registrations have no current session.", nameof(sessionId));
        if ((kind == SaveDataStateKind.Blocked) != (reason != SaveDataBlockReason.None))
            throw new ArgumentException("Only a blocked state has a block reason.", nameof(reason));
        if (kind == SaveDataStateKind.Blocked && sessionId == null && reason != SaveDataBlockReason.ServiceUnavailable)
            throw new ArgumentException("A save-specific block requires a session.", nameof(sessionId));
        Kind = kind; SessionId = sessionId; Reason = reason;
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }
}

/// <summary>Main-thread provider lifetime. Dispose once when the consumer stops, not on every session transition.</summary>
public interface ISaveDataRegistration : IDisposable
{
    SaveDataState State { get; }
    bool CanRead { get; }
    /// <summary>Live action gate, including callback dispatch and saves in flight. Never cache as permission.</summary>
    bool CanMutate { get; }
    /// <summary>No replay; transient callback dispatch alone does not emit this event.</summary>
    event Action<SaveDataState>? StateChanged;
}

public enum SaveDataRegistrationStatus
{
    Registered, Unavailable, SessionAlreadyStarted, DuplicateProvider, LimitExceeded, InvalidProvider
}

public sealed class SaveDataRegistrationResult
{
    public SaveDataRegistrationStatus Status { get; }
    public bool Succeeded => Status == SaveDataRegistrationStatus.Registered;
    /// <summary>Present only for a successful registration; refusal is an ordinary operation result.</summary>
    public ISaveDataRegistration? Registration { get; }
    public string Detail { get; }

    public SaveDataRegistrationResult(SaveDataRegistrationStatus status, ISaveDataRegistration? registration = null, string detail = "")
    {
        if (!Enum.IsDefined(typeof(SaveDataRegistrationStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == SaveDataRegistrationStatus.Registered) != (registration != null))
            throw new ArgumentException("Only a successful result has a registration.", nameof(registration));
        Status = status; Registration = registration;
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }
}

/// <summary>Additional custom mod data. Register before a session starts; API-owned content uses its own automatic saving.</summary>
public interface ISaveDataService : IServiceStatus
{
    SaveDataRegistrationResult Register(PersistenceProvider provider);
}
