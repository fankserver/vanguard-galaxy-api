using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class RecipeCatalogServiceTests
{
    private static LifecycleHub Bound()
    {
        var hub = new LifecycleHub((_, _) => { });
        hub.SetCapability("recipe-catalog", true, "Bound.");
        return hub;
    }
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
    public void MissingSourcesPreserveDiagnosisAndRefuseReads()
    {
        using var hub = Bound();
        hub.SetCapability("recipe-catalog", false, "Recipes disabled.", ServiceUnavailableReason.Disabled);
        hub.SetCapability("recipe-quotes", false, "Quotes not inspected.", ServiceUnavailableReason.UnsupportedGame);
        using var recipes = new RecipeCatalogService(hub, null, _ => { });
        using var quotes = new RecipeQuoteService(hub, null, _ => { });
        Assert.Equal(ServiceUnavailableReason.Disabled, recipes.Availability.Reason);
        Assert.Equal("Recipes disabled.", recipes.Availability.Detail);
        Assert.Equal(RecipeCatalogStatus.IntegrationUnavailable, recipes.Read().Status);
        Assert.Equal(ServiceUnavailableReason.UnsupportedGame, quotes.Availability.Reason);
        Assert.Null(quotes.CurrentStation);
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => recipes.Read()));
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.IRecipeCatalog"));
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.IRecipeQuotes"));
        Assert.Null(typeof(ModApi).GetProperty("Recipes"));
        Assert.Null(typeof(ModApi).GetProperty("RecipeQuotes"));
    }
    [Fact]
    public void HealthLossDuringQueryRejectsResultAndPreventsAnotherNativeRead()
    {
        using var hub = Bound();
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id); hub.GameplayInitialized(id);
        var source = new Source { OnRead = () => hub.SetCapability("recipe-catalog", false, "Bindings faulted.", ServiceUnavailableReason.ObserverFault) };
        using var service = new RecipeCatalogService(hub, source, _ => { });
        Assert.Equal(RecipeCatalogStatus.IntegrationUnavailable, service.Read().Status);
        Assert.Equal(RecipeCatalogStatus.IntegrationUnavailable, service.Read().Status);
        Assert.Equal(1, source.Reads);
    }
    [Fact]
    public void LifecycleAndDisposalGateNativeAccess()
    {
        using var hub = Bound(); var source = new Source();
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
        using var hub = Bound();
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id); hub.GameplayInitialized(id);
        var source = new Source { OnRead = () => hub.Begin(SessionOrigin.NewGame, null) };
        using var service = new RecipeCatalogService(hub, source, _ => { });
        Assert.Equal(RecipeCatalogStatus.SessionUnavailable, service.Read().Status);
    }
    [Fact]
    public void NativeAndReporterFailureDoNotManufactureEmptySuccess()
    {
        using var hub = Bound();
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id); hub.GameplayInitialized(id);
        var source = new Source { OnRead = () => throw new InvalidOperationException() };
        using var service = new RecipeCatalogService(hub, source, _ => throw new Exception());
        Assert.Equal(RecipeCatalogStatus.NativeFailure, service.Read().Status);
        source.OnRead = null; Assert.Equal(RecipeCatalogStatus.Available, service.Read().Status);
    }
}
