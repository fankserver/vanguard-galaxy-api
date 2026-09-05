using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class SaveTrackerTests
{
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly List<LifecycleEvent> _events = new();
    private readonly SaveTracker _tracker;
    public SaveTrackerTests() { _tracker = new SaveTracker(_hub); _hub.Subscribe("test", _events.Add); }
    private SaveTracker.Call Enter(object? data = null, int attempt = 0, bool skip = false, string path = "a.save") =>
        _tracker.Enter(data ?? new object(), path, 0, attempt, skip, null);
    private void File(SaveTracker.Call call) => _tracker.FileWritten(call.Data, call.Operation.Destination, call.Format);
    private void Metadata(SaveTracker.Call call) => _tracker.MetadataWritten(call.Operation.Destination);
    private void Write(SaveTracker.Call call) { File(call); Metadata(call); }
    private void Fail(SaveTracker.Call call) => _tracker.HandlingFailure(call.Data, call.Operation.Destination, call.Attempt);

    [Fact]
    public void SuccessRequiresBothClosedFileAndMetadata()
    {
        var call = Enter(); Write(call); _tracker.Exit(call, null);
        Assert.Equal(new[] { LifecycleEventKind.SaveStarted, LifecycleEventKind.SaveSucceeded }, _events.Select(e => e.Kind));
        Assert.Equal(_events[0].OperationId, _events[1].OperationId);
        Assert.Equal("a.save", _events[1].Destination);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public void NormalReturnWithoutCompleteWriteIsNotSuccess(bool file, bool metadata)
    {
        var call = Enter(); if (file) File(call); if (metadata) Metadata(call);
        _tracker.Exit(call, null); Assert.Equal(LifecycleEventKind.SaveFailed, _events.Last().Kind);
    }

    [Fact]
    public void EphemeralSkipIsNotSuccess()
    {
        var call = Enter(skip: true); _tracker.Exit(call, null);
        Assert.Equal(LifecycleEventKind.SaveSkipped, _events.Last().Kind);
    }

    [Fact]
    public void RetrySuccessHasOneStartedAndOneTerminalOutcome()
    {
        var data = new object(); var outer = Enter(data); Fail(outer);
        var retry = Enter(data, 1); Write(retry); _tracker.Exit(retry, null); _tracker.Exit(outer, null);
        Assert.Equal(new[] { LifecycleEventKind.SaveStarted, LifecycleEventKind.SaveSucceeded }, _events.Select(e => e.Kind));
    }

    [Fact]
    public void ExhaustedRetriesProduceOneFailureEvenWhenVanillaSwallowsException()
    {
        var data = new object(); var calls = new Stack<SaveTracker.Call>();
        for (int i = 0; i <= 5; i++) { calls.Push(Enter(data, i)); Fail(calls.Peek()); }
        while (calls.Count > 0) _tracker.Exit(calls.Pop(), null);
        Assert.Equal(new[] { LifecycleEventKind.SaveStarted, LifecycleEventKind.SaveFailed }, _events.Select(e => e.Kind));
    }

    [Fact]
    public void EscapingParentExceptionOverridesSuccessfulRetry()
    {
        var data = new object(); var outer = Enter(data); Fail(outer);
        var retry = Enter(data, 1); Write(retry); _tracker.Exit(retry, null);
        _tracker.Exit(outer, new InvalidOperationException());
        Assert.Equal(LifecycleEventKind.SaveFailed, _events.Last().Kind);
    }

    [Fact]
    public void FailureAfterFileAndMetadataIsStillFailure()
    {
        var call = Enter(); Write(call); Fail(call); _tracker.Exit(call, null);
        Assert.Equal(LifecycleEventKind.SaveFailed, _events.Last().Kind);
    }

    [Fact]
    public void NestedUnrelatedSaveDoesNotCompleteItsParent()
    {
        var outer = Enter(); var inner = Enter(path: "b.save"); Write(inner); _tracker.Exit(inner, null);
        _tracker.Exit(outer, null);
        Assert.Equal(new[] { LifecycleEventKind.SaveStarted, LifecycleEventKind.SaveStarted, LifecycleEventKind.SaveSucceeded, LifecycleEventKind.SaveFailed }, _events.Select(e => e.Kind));
        Assert.NotEqual(_events[0].OperationId, _events[1].OperationId);
    }

    [Fact]
    public void SamePayloadNestedCallWithoutFailureMarkerIsNotRetry()
    {
        var data = new object(); var outer = Enter(data); var inner = Enter(data, 1);
        Write(inner); _tracker.Exit(inner, null); _tracker.Exit(outer, null);
        Assert.Equal(2, _events.Count(e => e.Kind == LifecycleEventKind.SaveStarted));
    }

    [Fact]
    public void ReentrantSaveDuringStartedEventRemainsIndependent()
    {
        bool nested = false;
        _hub.Subscribe("reentrant", e =>
        {
            if (e.Kind != LifecycleEventKind.SaveStarted || nested) return;
            nested = true; var call = Enter(path: "b.save"); Write(call); _tracker.Exit(call, null);
        });
        var outer = Enter(); Write(outer); _tracker.Exit(outer, null);
        Assert.Equal(2, _events.Count(e => e.Kind == LifecycleEventKind.SaveSucceeded));
        Assert.Equal(2, _events.Select(e => e.OperationId).Distinct().Count());
    }

    [Fact]
    public void OperationRetainsCapturedSessionAndSlotAcrossSessionReplacement()
    {
        _hub.Begin(SessionOrigin.NewGame, null); var original = _hub.CurrentSession;
        var call = _tracker.Enter(new object(), "autosave-2.save", 0, 0, false, original);
        _hub.Begin(SessionOrigin.SaveLoad, "other.save"); Write(call); _tracker.Exit(call, null);
        Assert.Equal(original!.Id, _events.Last().Session!.Id);
        Assert.Equal("autosave-2.save", _events.Last().Destination);
    }

    [Fact]
    public void HelpersForAnotherPayloadOrDestinationCannotMarkWriteSuccessful()
    {
        var call = Enter();
        _tracker.FileWritten(new object(), "a.save", 0);
        _tracker.FileWritten(call.Data, "b.save", 0);
        _tracker.FileWritten(call.Data, "a.save", 1);
        _tracker.MetadataWritten("b.save");
        _tracker.Exit(call, null);
        Assert.Equal(LifecycleEventKind.SaveFailed, _events.Last().Kind);
    }

    [Fact]
    public void ForeignFailureHelperDoesNotMarkAnOtherwiseSuccessfulSaveFailed()
    {
        var call = Enter(); Write(call);
        _tracker.HandlingFailure(new object(), "a.save", 0);
        _tracker.HandlingFailure(call.Data, "b.save", 0);
        _tracker.HandlingFailure(call.Data, "a.save", 1);
        _tracker.Exit(call, null);
        Assert.Equal(LifecycleEventKind.SaveSucceeded, _events.Last().Kind);
    }
}
