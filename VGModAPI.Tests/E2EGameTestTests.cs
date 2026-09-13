using System;
using System.Reflection;
using VGModAPI.E2E;
using Xunit;

namespace VGModAPI.Tests;

public sealed class E2EGameTestTests
{
    [Fact]
    public void WaitingDoesNotRunLaterSteps()
    {
        var ready = false;
        var created = 0;
        var test = new GameTest(new[] {
            new TestStep("menu", "MainMenuUI.instance", () => ready),
            new TestStep("create", "CreateNewGamePlayer", () => { created++; return true; })
        }, 10);
        test.Tick(0);
        Assert.Equal(0, created);
        ready = true;
        test.Tick(1);
        test.Tick(2);
        test.Tick(3);
        Assert.True(test.Passed);
        Assert.True(test.Finished);
        Assert.Equal(1, created);
    }

    [Fact]
    public void TimeoutIsTerminalAndKeepsTheFailedBinding()
    {
        var created = false;
        var test = new GameTest(new[] {
            new TestStep("menu", "MainMenuUI.instance", () => false),
            new TestStep("create", "CreateNewGamePlayer", () => created = true)
        }, 10);
        test.Tick(0);
        test.Tick(10);
        test.Tick(11);
        Assert.True(test.Finished);
        Assert.False(test.Passed);
        Assert.False(created);
        Assert.Equal("MainMenuUI.instance", test.Binding);
        Assert.Contains("Timed out waiting for menu", test.Detail);
    }

    [Fact]
    public void SetupExceptionIsNamedAndCannotFallThroughToPassingAssertion()
    {
        var checkedGameplay = false;
        var test = new GameTest(new[] {
            new TestStep("create", "CreateNewGamePlayer", () => throw new TargetInvocationException(new InvalidOperationException("broken"))),
            new TestStep("assert", "GameplayManager.Start", () => checkedGameplay = true)
        }, 10);
        test.Tick(0);
        test.Tick(1);
        Assert.True(test.Finished);
        Assert.False(test.Passed);
        Assert.False(checkedGameplay);
        Assert.Equal("create: InvalidOperationException: broken", test.Detail);
        Assert.Equal("CreateNewGamePlayer", test.Binding);
    }

    [Fact]
    public void EmptyTestIsNotAFalsePass() =>
        Assert.Throws<ArgumentException>(() => new GameTest(Array.Empty<TestStep>(), 10));
}
