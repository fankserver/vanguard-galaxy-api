using System;
using System.Collections;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ObservedEnumeratorTests
{
    private int _depth;
    private IDisposable Enter() { _depth++; return new Scope(() => _depth--); }
    private sealed class Scope : IDisposable
    {
        private readonly Action _exit; internal Scope(Action exit) => _exit = exit;
        public void Dispose() => _exit();
    }
    private static void Drain(IEnumerator routine)
    {
        while (routine.MoveNext()) if (routine.Current is IEnumerator child) Drain(child);
    }

    [Fact]
    public void NestedCoroutineHasContextAndPreservesOrdinaryYieldObjects()
    {
        int completed = 0; var marker = new object(); var seen = new List<object?>();
        IEnumerator Child() { Assert.Equal(1, _depth); yield return marker; Assert.Equal(1, _depth); }
        IEnumerator Root() { Assert.Equal(1, _depth); yield return Child(); yield return null; }
        using var observed = new ObservedEnumerator(Root(), Enter, _ => Assert.Fail("unexpected error"), () => completed++);
        Assert.True(observed.MoveNext()); Assert.Equal(0, _depth);
        var child = Assert.IsAssignableFrom<IEnumerator>(observed.Current);
        Assert.Same(child, observed.Current);
        while (child.MoveNext()) seen.Add(child.Current);
        Assert.True(observed.MoveNext()); Assert.Null(observed.Current);
        Assert.False(observed.MoveNext()); Assert.False(observed.MoveNext());
        Assert.Equal(1, completed); Assert.Equal(new[] { marker }, seen); Assert.Equal(0, _depth);
    }

    [Fact]
    public void NestedExceptionIsReportedAndRethrownUnchanged()
    {
        var expected = new InvalidOperationException("original"); Exception? reported = null;
        IEnumerator Child() { yield return null; throw expected; }
        IEnumerator Root() { yield return Child(); }
        using var observed = new ObservedEnumerator(Root(), Enter, ex => reported = ex);
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => Drain(observed)));
        Assert.Same(expected, reported); Assert.Equal(0, _depth);
    }

    [Fact]
    public void DisposingUnfinishedRootReportsCancellationOnce()
    {
        int failures = 0; bool disposed = false;
        IEnumerator Root() { try { yield return null; yield return null; } finally { disposed = true; } }
        var observed = new ObservedEnumerator(Root(), Enter, ex => { Assert.IsType<OperationCanceledException>(ex); failures++; }, () => Assert.Fail("not completed"));
        Assert.True(observed.MoveNext()); observed.Dispose(); observed.Dispose();
        Assert.True(disposed); Assert.Equal(1, failures); Assert.False(observed.MoveNext()); Assert.Equal(0, _depth);
    }

    [Fact]
    public void SuccessfulCompletionDoesNotReportCancellationOnDispose()
    {
        int failures = 0, completed = 0;
        IEnumerator Empty() { yield break; }
        var observed = new ObservedEnumerator(Empty(), Enter, _ => failures++, () => completed++);
        Drain(observed); observed.Dispose();
        Assert.Equal(0, failures); Assert.Equal(1, completed);
    }
}
