using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>Tracks synchronous Store calls. Only matching data/name/format/attempt chains are retries.</summary>
internal sealed class SaveTracker
{
    private readonly LifecycleHub _hub;
    private readonly Stack<Call> _stack = new();
    internal SaveTracker(LifecycleHub hub) => _hub = hub;

    internal Call Enter(object data, string destination, object format, int attempt, bool skipped, SessionSnapshot? session)
    {
        _hub.CheckThread();
        Call? parent = _stack.Count > 0 ? _stack.Peek() : null;
        bool retry = parent != null && parent.ExpectRetry && ReferenceEquals(parent.Data, data)
            && parent.Operation.Destination == destination && Equals(parent.Format, format) && attempt == parent.Attempt + 1;
        if (retry) parent!.ExpectRetry = false;
        var operation = retry ? parent!.Operation : new Operation(destination, session);
        var call = new Call(operation, data, format, attempt, !retry, skipped);
        _stack.Push(call);
        if (call.IsRoot)
            _hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, session, operation.Id, destination));
        return call;
    }

    internal void FileWritten(object data, string destination, object format)
    {
        if (_stack.Count == 0) return;
        var call = _stack.Peek();
        if (ReferenceEquals(call.Data, data) && call.Operation.Destination == destination && Equals(call.Format, format))
            call.FileWritten = true;
    }
    internal void MetadataWritten(string destination)
    {
        if (_stack.Count > 0 && _stack.Peek().Operation.Destination == destination)
            _stack.Peek().MetadataWritten = true;
    }
    internal void HandlingFailure(object data, string destination, int attempt)
    {
        if (_stack.Count == 0) return;
        var call = _stack.Peek();
        if (!ReferenceEquals(call.Data, data) || call.Operation.Destination != destination || call.Attempt != attempt) return;
        call.Failed = true;
        call.ExpectRetry = call.Attempt < 5;
    }

    internal void Exit(Call call, Exception? error)
    {
        _hub.CheckThread();
        if (_stack.Count == 0 || !ReferenceEquals(_stack.Peek(), call))
            throw new InvalidOperationException("Unbalanced save-operation scopes.");
        _stack.Pop();
        // The innermost retry establishes the outcome. Unwinding parents must not replace it.
        if (!call.Operation.HasOutcome)
        {
            call.Operation.Outcome = error != null || call.Failed ? LifecycleEventKind.SaveFailed
                : call.Skipped ? LifecycleEventKind.SaveSkipped
                : call.FileWritten && call.MetadataWritten ? LifecycleEventKind.SaveSucceeded
                : LifecycleEventKind.SaveFailed;
            call.Operation.Detail = error?.GetType().Name ?? (call.Skipped ? "Ephemeral player: vanilla skipped this write."
                : call.Operation.Outcome == LifecycleEventKind.SaveFailed ? "Vanilla write did not complete successfully." : null);
            call.Operation.HasOutcome = true;
        }
        // An exception escaping a parent invalidates any successful inner retry result.
        if (error != null)
        {
            call.Operation.Outcome = LifecycleEventKind.SaveFailed;
            call.Operation.Detail = error.GetType().Name;
        }
        if (call.IsRoot)
            _hub.Publish(new LifecycleEvent(call.Operation.Outcome, call.Operation.Session,
                call.Operation.Id, call.Operation.Destination, call.Operation.Detail));
    }

    internal sealed class Operation
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal readonly string Destination;
        internal readonly SessionSnapshot? Session;
        internal bool HasOutcome;
        internal LifecycleEventKind Outcome;
        internal string? Detail;
        internal Operation(string destination, SessionSnapshot? session) { Destination = destination; Session = session; }
    }

    internal sealed class Call
    {
        internal readonly Operation Operation;
        internal readonly object Data;
        internal readonly object Format;
        internal readonly int Attempt;
        internal readonly bool IsRoot;
        internal readonly bool Skipped;
        internal bool FileWritten, MetadataWritten, Failed, ExpectRetry;
        internal Call(Operation operation, object data, object format, int attempt, bool root, bool skipped)
        { Operation = operation; Data = data; Format = format; Attempt = attempt; IsRoot = root; Skipped = skipped; }
    }
}
