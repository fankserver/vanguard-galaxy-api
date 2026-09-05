using System;
using System.Collections.Generic;

namespace VGModAPI;

public enum SessionPhase { None, Starting, PlayerReady, GameplayInitialized, Failed, Invalidated }
public enum SessionOrigin { SaveLoad, NewGame }
public enum LifecycleEventKind { SessionStarting, SessionInvalidated, PlayerReady, GameplayInitialized, SessionStartFailed, SaveStarted, SaveSucceeded, SaveSkipped, SaveFailed }

/// <summary>A runtime attempt, not a campaign or save identity. No vanilla object references are exposed.</summary>
public sealed class SessionSnapshot
{
    public Guid Id { get; }
    public SessionPhase Phase { get; }
    public SessionOrigin Origin { get; }
    public string? SavePath { get; }

    public SessionSnapshot(Guid id, SessionPhase phase, SessionOrigin origin, string? savePath)
    { Id = id; Phase = phase; Origin = origin; SavePath = savePath; }
}

public sealed class LifecycleEvent
{
    public LifecycleEventKind Kind { get; }
    public SessionSnapshot? Session { get; }
    public Guid? OperationId { get; }
    public string? Destination { get; }
    public string? Detail { get; }

    public LifecycleEvent(LifecycleEventKind kind, SessionSnapshot? session, Guid? operationId = null, string? destination = null, string? detail = null)
    { Kind = kind; Session = session; OperationId = operationId; Destination = destination; Detail = detail; }
}

public sealed class CapabilityStatus
{
    public string Name { get; }
    public bool Available { get; }
    public bool RuntimeQualified { get; }
    public string Detail { get; }
    public CapabilityStatus(string name, bool available, bool runtimeQualified, string detail)
    { Name = name; Available = available; RuntimeQualified = runtimeQualified; Detail = detail; }
}

/// <summary>All members and subscription disposal are Unity-main-thread-only. Registration does not replay events.</summary>
public interface ILifecycleApi
{
    SessionSnapshot? CurrentSession { get; }
    IReadOnlyList<CapabilityStatus> Capabilities { get; }
    IDisposable Subscribe(string owner, Action<LifecycleEvent> callback);
}

/// <summary>Optional since 0.1.1. Main-thread-only; false is not a readiness or mutation guarantee.</summary>
public interface ILifecycleDispatchState
{
    /// <summary>True throughout callback delivery, including queued reentrant events and error reporting.</summary>
    bool IsDispatchingCallbacks { get; }
}

/// <summary>Available after the API plugin's Awake; declare a hard BepInEx dependency on vgmodapi.</summary>
public static class ModApi
{
    public const string PluginId = "vgmodapi";
    public static ILifecycleApi? Current { get; internal set; }
}
