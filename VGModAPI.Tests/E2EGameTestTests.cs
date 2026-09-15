using System;
using System.Reflection;
using VGModAPI.E2E;
using Xunit;

namespace VGModAPI.Tests;

public sealed class E2EGameTestTests
{
    [Fact]
    public void WaitingRetainsObservationAndDoesNotRunLaterSteps()
    {
        var ready = false;
        var created = 0;
        var test = new GameTest(new[] {
            new TestStep("menu", "MainMenuUI.instance", 5, () => ready
                ? StepResult.Pass("menu ready") : StepResult.Wait("menu instance is null")),
            new TestStep("create", "CreateNewGamePlayer", 5, () => { created++; return StepResult.Pass(); })
        }, 20);
        test.Tick(0);
        Assert.Equal("menu instance is null", test.Observation);
        Assert.Equal(0, created);
        ready = true;
        test.Tick(1);
        test.Tick(2);
        Assert.True(test.Passed);
        Assert.Equal(1, created);
    }

    [Fact]
    public void PerStepTimeoutIncludesLastObservationAndKeepsBinding()
    {
        var test = new GameTest(new[] {
            new TestStep("attach", "IDungeonProvider.Attach", 3,
                () => StepResult.Wait("status=StaleTarget; targets=0; poi=salvage"))
        }, 30);
        test.Tick(4);
        test.Tick(7);
        Assert.True(test.Finished);
        Assert.False(test.Passed);
        Assert.Equal("IDungeonProvider.Attach", test.Binding);
        Assert.Contains("Step exceeded 3s", test.Detail);
        Assert.Contains("status=StaleTarget; targets=0; poi=salvage", test.Detail);
    }

    [Fact]
    public void OverallTimeoutIncludesLastObservation()
    {
        var test = new GameTest(new[] {
            new TestStep("menu", "MainMenuUI.instance", 100, () => StepResult.Wait("menu instance is null"))
        }, 10);
        test.Tick(0);
        test.Tick(10);
        Assert.Contains("Overall deadline expired", test.Detail);
        Assert.Contains("menu instance is null", test.Detail);
    }

    [Fact]
    public void ExplicitFailureIsTerminal()
    {
        var test = new GameTest(new[] {
            new TestStep("attach", "IDungeonProvider.Attach", 5,
                () => StepResult.Fail("Rejected: target belongs to another provider"))
        }, 20);
        test.Tick(0);
        Assert.True(test.Finished);
        Assert.False(test.Passed);
        Assert.Contains("Rejected: target belongs to another provider", test.Detail);
    }

    [Fact]
    public void ActionThenWaitPerformsAcceptedActionOnlyOnce()
    {
        var actions = 0;
        var ready = false;
        var test = new GameTest(new[] {
            TestStep.ActionThenWait("offer", "Offer / State", 5,
                () => { actions++; return StepResult.Pass("offer accepted"); },
                () => ready ? StepResult.Pass("active") : StepResult.Wait("state=Offered"))
        }, 20);
        test.Tick(0);
        test.Tick(1);
        Assert.Equal(1, actions);
        Assert.Equal("state=Offered", test.Observation);
        ready = true;
        test.Tick(2);
        Assert.True(test.Passed);
        Assert.Equal(1, actions);
    }

    [Fact]
    public void SetupExceptionIsNamedAndCannotFallThroughToPassingAssertion()
    {
        var checkedGameplay = false;
        var test = new GameTest(new[] {
            new TestStep("create", "CreateNewGamePlayer", 5,
                () => throw new TargetInvocationException(new InvalidOperationException("broken"))),
            new TestStep("assert", "GameplayManager.Start", 5,
                () => { checkedGameplay = true; return StepResult.Pass(); })
        }, 20);
        test.Tick(0);
        Assert.True(test.Finished);
        Assert.False(test.Passed);
        Assert.False(checkedGameplay);
        Assert.Equal("create: InvalidOperationException: broken", test.Detail);
    }

    [Fact]
    public void WaitingAndFailureRequireDiagnosticDetail()
    {
        Assert.Throws<ArgumentException>(() => StepResult.Wait(" "));
        Assert.Throws<ArgumentException>(() => StepResult.Fail(""));
    }

    [Fact]
    public void EmptyTestIsNotAFalsePass() =>
        Assert.Throws<ArgumentException>(() => new GameTest(Array.Empty<TestStep>(), 10));
}
