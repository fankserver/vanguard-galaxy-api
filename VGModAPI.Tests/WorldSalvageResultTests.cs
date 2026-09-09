using System.IO;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldSalvageResultTests
{
    [Fact]
    public void ExactResultRequiresCompletionAndOriginalLivePoi()
    {
        var results = new WorldSalvageResults(); var poi = new object(); var result = new object(); bool live = true;
        var receipt = results.Begin(poi, new object(), () => true, () => live);
        results.Reserve(receipt, result);
        Assert.Throws<InvalidDataException>(() => results.RequirePublication(poi, result));
        results.Complete(receipt, result); results.RequirePublication(poi, result);
        Assert.Throws<InvalidDataException>(() => results.RequirePublication(new object(), result));
        results.RequirePublication(poi, result);
        live = false; Assert.Throws<InvalidDataException>(() => results.RequirePublication(poi, result));
        live = true; Assert.Throws<InvalidDataException>(() => results.RequirePublication(poi, result));
        results.RequirePublication(poi, new object()); // Unclassified vanilla data is not owned by this registry.
    }
    [Fact]
    public void FailedReservationAndAmbiguousConstructorsNeverBecomeVanilla()
    {
        var results = new WorldSalvageResults(); var poi = new object(); var first = new object(); var second = new object();
        var receipt = results.Begin(poi, new object(), () => false, () => true);
        Assert.Throws<InvalidDataException>(() => results.Reserve(receipt, first));
        Assert.Throws<InvalidDataException>(() => results.Reserve(receipt, second));
        Assert.Throws<InvalidDataException>(() => results.RequirePublication(poi, first));
        Assert.Throws<InvalidDataException>(() => results.RequirePublication(poi, second));
    }
    [Fact]
    public void AdmissionMutationCannotLeaveStaleCompletionAuthority()
    {
        var results = new WorldSalvageResults(); var poi = new object(); var value = new object(); bool mutate = false, live = true;
        var receipt = results.Begin(poi, new object(), () => { if (mutate) live = false; return true; }, () => live);
        results.Reserve(receipt, value); mutate = true;
        Assert.Throws<InvalidDataException>(() => results.Complete(receipt, value));
        Assert.Throws<InvalidDataException>(() => results.RequirePublication(poi, value));
    }
}
