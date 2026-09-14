using System;
using System.Collections.Generic;

namespace VGModAPI.E2E;

internal enum StepStatus { Waiting, Passed, Failed }

internal readonly struct StepResult
{
    internal StepStatus Status { get; }
    internal string Detail { get; }
    private StepResult(StepStatus status, string detail) { Status = status; Detail = detail ?? ""; }
    internal static StepResult Wait(string detail) => new(StepStatus.Waiting, Required(detail));
    internal static StepResult Pass(string detail = "") => new(StepStatus.Passed, detail);
    internal static StepResult Fail(string detail) => new(StepStatus.Failed, Required(detail));
    private static string Required(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("A waiting/failure result requires diagnostic detail.", nameof(value)) : value;
}

internal readonly struct StepContext
{
    internal double ElapsedSeconds { get; }
    internal double TimeoutSeconds { get; }
    internal StepContext(double elapsedSeconds, double timeoutSeconds)
    { ElapsedSeconds = elapsedSeconds; TimeoutSeconds = timeoutSeconds; }
}

internal sealed class TestStep
{
    internal string Name { get; }
    internal string Binding { get; }
    internal double TimeoutSeconds { get; }
    internal Func<StepContext, StepResult> Poll { get; }

    internal TestStep(string name, string binding, Func<bool> poll)
        : this(name, binding, 45, _ => poll()
            ? StepResult.Pass()
            : StepResult.Wait("Condition not yet satisfied; inspect " + binding)) { }

    internal TestStep(string name, string binding, double timeoutSeconds, Func<StepResult> poll)
        : this(name, binding, timeoutSeconds, _ => poll()) { }

    internal TestStep(string name, string binding, double timeoutSeconds, Func<StepContext, StepResult> poll)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A step needs a name.", nameof(name));
        if (string.IsNullOrWhiteSpace(binding)) throw new ArgumentException("A step needs an inspection binding.", nameof(binding));
        if (timeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        Name = name; Binding = binding; TimeoutSeconds = timeoutSeconds; Poll = poll ?? throw new ArgumentNullException(nameof(poll));
    }

    internal static TestStep Action(string name, string binding, double timeoutSeconds,
        Func<StepResult> attempt) => new(name, binding, timeoutSeconds, attempt);

    internal static TestStep ActionThenWait(string name, string binding, double timeoutSeconds,
        Func<StepResult> action, Func<StepResult> expectation)
    {
        var acted = false;
        return new TestStep(name, binding, timeoutSeconds, () =>
        {
            if (!acted)
            {
                var actionResult = action();
                if (actionResult.Status != StepStatus.Passed) return actionResult;
                acted = true;
            }
            return expectation();
        });
    }
}

/// <summary>Frame-driven execution with explicit outcomes, per-step deadlines and one overall safety deadline.
/// No Unity dependency: the same execution logic is tested by VGModAPI.Tests.</summary>
internal sealed class GameTest
{
    private readonly IReadOnlyList<TestStep> _steps;
    private readonly double _deadline;
    private int _index;
    private double? _stepStarted;
    internal bool Finished { get; private set; }
    internal bool Passed { get; private set; }
    internal string Detail { get; private set; } = "";
    internal string Binding { get; private set; } = "";
    internal string CurrentStep => Finished ? "" : _steps[_index].Name;
    internal string Observation { get; private set; } = "not polled yet";
    internal double StepElapsedSeconds { get; private set; }
    internal double StepTimeoutSeconds => Finished ? 0 : _steps[_index].TimeoutSeconds;

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
        _stepStarted ??= elapsedSeconds;
        StepElapsedSeconds = elapsedSeconds - _stepStarted.Value;
        try
        {
            if (elapsedSeconds >= _deadline)
                throw new TimeoutException($"Overall deadline expired; last observation: {Observation}");
            if (StepElapsedSeconds >= step.TimeoutSeconds)
                throw new TimeoutException($"Step exceeded {step.TimeoutSeconds:0.###}s; last observation: {Observation}");

            var result = step.Poll(new StepContext(StepElapsedSeconds, step.TimeoutSeconds));
            Observation = result.Detail;
            if (result.Status == StepStatus.Waiting) return;
            if (result.Status == StepStatus.Failed)
                throw new InvalidOperationException(result.Detail);
            if (++_index == _steps.Count)
            {
                Finished = Passed = true;
                Detail = "All gameplay assertions passed.";
                Binding = "";
                Observation = result.Detail;
                return;
            }
            _stepStarted = elapsedSeconds;
            StepElapsedSeconds = 0;
            Observation = "not polled yet";
        }
        catch (Exception ex)
        {
            Finished = true;
            var cause = ex.GetBaseException();
            Detail = step.Name + ": " + cause.GetType().Name + ": " + cause.Message;
        }
    }
}
