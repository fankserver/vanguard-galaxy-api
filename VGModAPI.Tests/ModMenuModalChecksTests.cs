using System;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModMenuModalChecksTests
{
    [Fact]
    public void DeferredNativeStartupIsNotComparedWithThePreStartTimeScale()
    {
        var open = true;
        var scale = 1f; // Show creates the popup, but Start has not run.
        Assert.Throws<InvalidOperationException>(() => ModMenuModalChecks.AfterNativeStart(open, scale));
        scale = 0f; // The next frame runs native Start/Pause.
        ModMenuModalChecks.AfterNativeStart(open, scale);
        Assert.Throws<InvalidOperationException>(() => ModMenuModalChecks.AfterNativeDestroy(open, scale));
        open = false;
        scale = 1f; // Native OnDestroy/Unpause restores the initially unpaused menu.
        ModMenuModalChecks.AfterNativeDestroy(open, scale);
    }

    [Fact]
    public void PrematureUnpauseAndFailedRestorationAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => ModMenuModalChecks.AfterNativeStart(true, 1f));
        Assert.Throws<InvalidOperationException>(() => ModMenuModalChecks.AfterNativeDestroy(false, 0f));
    }
}
