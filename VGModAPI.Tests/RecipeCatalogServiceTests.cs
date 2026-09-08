using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class RecipeCatalogServiceTests
{
    private sealed class Source : IRecipeCatalogSource
    {
        internal int Reads;
        internal Action? OnRead;
        public RecipeCatalogSnapshot Read(Guid sessionId, bool includeUnavailable)
        {
            Reads++; OnRead?.Invoke();
            return new RecipeCatalogSnapshot(RecipeCatalogStatus.Available, sessionId, "", Array.Empty<RecipeSnapshot>());
        }
    }
    [Fact]
    public void LifecycleAndDisposalGateNativeAccess()
    {
        using var hub = new LifecycleHub((_, _) => { }); var source = new Source();
        using var service = new RecipeCatalogService(hub, source, _ => { });
        Assert.Equal(RecipeCatalogStatus.SessionUnavailable, service.Read().Status); Assert.Equal(0, source.Reads);
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id);
        Assert.Equal(RecipeCatalogStatus.SessionUnavailable, service.Read().Status);
        hub.GameplayInitialized(id);
        Assert.Equal(RecipeCatalogStatus.Available, service.Read().Status);
        Assert.Equal(RecipeCatalogStatus.Available, service.Read().Status); Assert.Equal(2, source.Reads);
        service.Dispose(); Assert.Equal(RecipeCatalogStatus.IntegrationUnavailable, service.Read().Status);
    }
    [Fact]
    public void SessionReplacementDuringQueryRejectsStaleResult()
    {
        using var hub = new LifecycleHub((_, _) => { });
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id); hub.GameplayInitialized(id);
        var source = new Source { OnRead = () => hub.Begin(SessionOrigin.NewGame, null) };
        using var service = new RecipeCatalogService(hub, source, _ => { });
        Assert.Equal(RecipeCatalogStatus.SessionUnavailable, service.Read().Status);
    }
    [Fact]
    public void NativeAndReporterFailureDoNotManufactureEmptySuccess()
    {
        using var hub = new LifecycleHub((_, _) => { });
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id); hub.GameplayInitialized(id);
        var source = new Source { OnRead = () => throw new InvalidOperationException() };
        using var service = new RecipeCatalogService(hub, source, _ => throw new Exception());
        Assert.Equal(RecipeCatalogStatus.NativeFailure, service.Read().Status);
        source.OnRead = null; Assert.Equal(RecipeCatalogStatus.Available, service.Read().Status);
    }
}
