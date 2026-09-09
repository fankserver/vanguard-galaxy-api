# Deferred gameplay actions

`ModApi.Services.Actions` lets an Abstractions-only consumer react to an observed
event without owning a `MonoBehaviour`, pending flags, or an update loop.
Observation callbacks remain observational. Actions run at a later API
`LateUpdate`, **not** when a callback dispatch scope unwinds inside vanilla.

```csharp
// In an event handler; use the event's session identity, not a fresh session lookup.
ModApi.Services.Actions.Defer(
    "my.plugin", observedSessionId,
    action: () => AdvanceStoryBeat(),
    completed: outcome => {
        if (outcome != DeferredActionOutcome.Executed)
            LogReactionOutcome(outcome);
    },
    saveData: registration);
```

## Gates and lifetime

All access, callbacks and handle disposal are main-thread-only. Supply a stable
plugin owner ID and the **observed** session ID. A stale event cannot accidentally
schedule against a replacement session, including another load of the same save.
The API requires available session/save observation, the same initialized session,
no observed save operation in flight, and no callback dispatch. Without those
observers it refuses work; it does not guess a safe boundary on unsupported builds.

Pass your `ISaveDataRegistration` when changing your custom saved state. The API
checks its live `CanMutate` immediately before execution. Restoration, capture and
publication gates are not bypassed. Transient save/callback restrictions wait;
blocked or disposed registration state terminates with `SaveDataBlocked`.
Registration is optional for consumers without custom save data.

The optional `canRun` predicate waits for additional domain readiness. It runs
observationally under callback guards, so **do not put `CanMutate` in it**; pass the
registration instead. Neither predicate nor action runs inline in `Defer`.
A predicate returning false retains the action until a later boundary. There is
no guessed timeout: dispose its handle when its domain context is abandoned.
Predicates should be short and side-effect-free. The action must still inspect
operation results: this is not universal world/UI readiness or a promise that
any particular spawn, dialogue, inventory or story command will succeed.

Actions never run between an observed `SaveStarted` and its terminal event,
including nested saves. They cannot affect the snapshot already captured for that
save; their mutations belong to a subsequent save. This service neither initiates
saves nor makes game/mod storage atomic.

## Ordering, cancellation and outcomes

- Accepted work executes in enqueue order **within each owner**. Waiting work
  blocks later actions of that owner, not other owners. Use one stable owner ID
  rather than manufacturing IDs to bypass ordering. IDs are not a security boundary.
- Eligible owners are visited in enqueue order. Actions are never coalesced.
- Each boundary considers only its initial queue snapshot and executes at most
  64 actions. Work enqueued by actions, predicates or completion callbacks waits
  for another boundary; nested drains do not recurse.
- The queue holds at most 1024 pending actions across owners. `QueueFull` and other
  admission refusals call `completed` **synchronously before `Defer` returns**.
  Accepted actions are not evicted to admit newer ones.
- Every valid call completes exactly once: `Executed`, `Faulted`, `SessionEnded`,
  `SaveDataBlocked`, `Cancelled`, `Unavailable`, or `QueueFull`. Argument/thread
  errors throw before admission instead. `Executed` means the delegate returned,
  not that every gameplay command inside it succeeded. Faulted actions are not
  retried or rolled back; some effects might already have happened.
- Session invalidation/replacement/start failure drops its pending actions even
  without a later update. Loss of observer availability is checked at admission
  and each boundary; API shutdown releases pending work. Shutdown may report
  `SessionEnded` through invalidation or `Unavailable` through service disposal.
- Disposing the returned handle cancels pending work and completes it as
  `Cancelled`. Repeated disposal, or disposal after execution starts, is inert.
- Completion callbacks are observational and guarded. They may enqueue another
  action, but must not blindly retry an admission refusal: refusals are synchronous.
  Action, predicate, completion and diagnostic exceptions are individually isolated.

No consumer loop is needed. Retain handles only when explicit cancellation is
useful, and handle dropped one-shot story reactions visibly rather than treating
them as executed. See the [lifecycle contract](lifecycle-contract.md) and
[save-data contract](persistence-storage.md) for the underlying gates.
