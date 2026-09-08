using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BoardingCrewTransferTests
{
    [Fact]
    public void FullManifestIsValidatedBeforeAnyDebit()
    {
        var roster = new Dictionary<string, int> { ["Marine"] = 3, ["Gunner"] = 1 };
        var request = new BoardingCrewManifest(new Dictionary<string, int> { ["Marine"] = 2, ["Gunner"] = 2 });
        Assert.Equal(BoardingCommandStatus.InsufficientCrew, BoardingCrewTransfer.Transfer(roster, request, _ => true, 10,
            _ => throw new Exception("Transport must not run."), () => throw new Exception("Notify must not run.")));
        Assert.Equal(3, roster["Marine"]); Assert.Equal(1, roster["Gunner"]);
    }
    [Fact]
    public void DebitPrecedesTransportAndNotificationRunsOnceAfterDispatch()
    {
        var roster = new Dictionary<string, int> { ["Marine"] = 3, ["Gunner"] = 2 };
        var request = new BoardingCrewManifest(new Dictionary<string, int> { ["Marine"] = 2, ["Gunner"] = 1 });
        var transported = false; var notifications = 0;
        Assert.Equal(BoardingCommandStatus.Admitted, BoardingCrewTransfer.Transfer(roster, request, _ => true, 10, crew =>
        {
            Assert.Equal(1, roster["Marine"]); Assert.Equal(1, roster["Gunner"]); Assert.Equal(0, notifications);
            Assert.Equal(2, crew["Marine"]); transported = true;
        }, () => { Assert.True(transported); notifications++; }));
        Assert.Equal(1, notifications);
        Assert.Equal(BoardingCommandStatus.InsufficientCrew, BoardingCrewTransfer.Transfer(roster, request, _ => true, 10,
            _ => throw new Exception(), () => throw new Exception()));
    }
    [Fact]
    public void PartialNativeFailureDoesNotRefundOrRetryTransport()
    {
        var roster = new Dictionary<string, int> { ["Marine"] = 3 };
        var request = new BoardingCrewManifest(new Dictionary<string, int> { ["Marine"] = 2 });
        var error = new InvalidOperationException("pod creation failed after one pod"); var calls = 0; var notifications = 0;
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => BoardingCrewTransfer.Transfer(roster, request, _ => true, 10,
            _ => { calls++; throw error; }, () => notifications++)));
        Assert.Equal(1, roster["Marine"]); Assert.Equal(1, calls); Assert.Equal(1, notifications);
    }
    [Fact]
    public void NotificationFailureCannotHideNativeTransportFailure()
    {
        var roster = new Dictionary<string, int> { ["Marine"] = 1 };
        var request = new BoardingCrewManifest(roster);
        var native = new InvalidOperationException("native"); var notification = new ArgumentException("subscriber");
        var error = Assert.Throws<AggregateException>(() => BoardingCrewTransfer.Transfer(roster, request, _ => true, 1,
            _ => throw native, () => throw notification));
        Assert.Equal(new Exception[] { native, notification }, error.InnerExceptions);
        Assert.Equal(0, roster["Marine"]);
    }
}
