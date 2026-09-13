using System;
using System.Collections.Generic;

namespace VGModAPI.E2E;

internal sealed class TestStep
{
    internal string Name { get; }
    internal string Binding { get; }
    internal Func<bool> Advance { get; }
    internal TestStep(string name, string binding, Func<bool> advance)
    { Name = name; Binding = binding; Advance = advance; }
}

/// <summary>Frame-driven execution with one monotonic deadline and terminal failures.
/// No Unity dependency: the same execution logic is tested by VGModAPI.Tests.</summary>
internal sealed class GameTest
{
    private readonly IReadOnlyList<TestStep> _steps;
    private readonly double _deadline;
    private int _index;
    internal bool Finished { get; private set; }
    internal bool Passed { get; private set; }
    internal string Detail { get; private set; } = "";
    internal string Binding { get; private set; } = "";

    internal GameTest(IReadOnlyList<TestStep> steps, double deadline)
    {
        if (steps.Count == 0) throw new ArgumentException("A game test needs at least one step.", nameof(steps));
        _steps = steps;
        _deadline = deadline;
    }

    internal void Tick(double elapsedSeconds)
    {
        if (Finished) return;
        var step = _steps[_index];
        Binding = step.Binding;
        try
        {
            if (elapsedSeconds >= _deadline)
                throw new TimeoutException("Timed out waiting for " + step.Name);
            if (!step.Advance()) return;
            if (++_index == _steps.Count)
            {
                Finished = Passed = true;
                Detail = "All gameplay assertions passed.";
                Binding = "";
            }
        }
        catch (Exception ex)
        {
            Finished = true;
            var cause = ex.GetBaseException();
            Detail = step.Name + ": " + cause.GetType().Name + ": " + cause.Message;
        }
    }
}
