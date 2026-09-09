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
    /// <param name="owner">Stable plugin ID; pending actions execute FIFO within this owner.</param>
    /// <param name="expectedSessionId">Session identity from the observed event, not a fresh current-session lookup.</param>
    /// <param name="action">Gameplay reaction; operation results still require inspection.</param>
    /// <param name="completed">Observational exactly-once outcome callback. Admission refusals invoke it inline.</param>
    /// <param name="saveData">Custom save-data registration whose live CanMutate gate must be open.</param>
    /// <param name="canRun">Optional observational domain-readiness predicate. Do not check CanMutate here:
    /// predicates run under callback guards; pass saveData instead. False blocks later work of this owner
    /// until readiness or cancellation.</param>
    IDisposable Defer(string owner, Guid expectedSessionId, Action action, Action<DeferredActionOutcome> completed,
        ISaveDataRegistration? saveData = null, Func<bool>? canRun = null);
}
