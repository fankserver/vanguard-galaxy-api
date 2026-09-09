using System;

namespace VGModAPI;

public enum DeferredActionOutcome
{
    Executed, Faulted, SessionEnded, SaveDataBlocked, Cancelled, Unavailable, QueueFull
}

/// <summary>Main-thread, session-scoped reactions executed by the API at a later update boundary.</summary>
public interface IDeferredActionService
{
    /// <summary>
    /// Queue an action for the observed session. Completion is required and invoked exactly once,
    /// including rejection. Pass the consumer's save-data registration when the action mutates its data.
    /// An optional observational readiness predicate can wait for a domain-specific context.
    /// Dispose the returned handle to cancel pending work. Neither actions nor predicates run inline.
    /// </summary>
    IDisposable Defer(string owner, Guid expectedSessionId, Action action, Action<DeferredActionOutcome> completed,
        ISaveDataRegistration? saveData = null, Func<bool>? canRun = null);
}
