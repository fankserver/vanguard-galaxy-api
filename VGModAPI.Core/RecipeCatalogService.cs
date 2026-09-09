using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

internal sealed class RecipeCatalogLimitException : Exception { }

internal interface IRecipeCatalogSource
{
    RecipeCatalogSnapshot Read(Guid sessionId, bool includeUnavailable);
}

internal sealed class RecipeCatalogService : IRecipeService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IRecipeCatalogSource? _source;
    private readonly IServiceStatus _status;
    private readonly Action<Exception> _report;
    private bool _disposed;
    internal RecipeCatalogService(LifecycleHub hub, IRecipeCatalogSource? source, Action<Exception> report)
    {
        _hub = hub; _source = source; _report = report;
        _status = hub.Services.Get("recipe-catalog");
        if (source == null && _status.Availability.IsAvailable)
            hub.SetCapability("recipe-catalog", false, "Recipe bindings unavailable.");
    }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public RecipeCatalogSnapshot Read(bool includeUnavailable = false)
    {
        _hub.CheckThread();
        var session = _hub.CurrentSession;
        if (_disposed || _source == null || !Availability.IsAvailable)
            return Failure(RecipeCatalogStatus.IntegrationUnavailable, session?.Id, Availability.Detail);
        if (session?.Phase != SessionPhase.GameplayInitialized)
            return Failure(RecipeCatalogStatus.SessionUnavailable, session?.Id, "Gameplay session required.");
        try
        {
            var result = _source.Read(session.Id, includeUnavailable);
            if (!Availability.IsAvailable) return Failure(RecipeCatalogStatus.IntegrationUnavailable, session.Id, Availability.Detail);
            if (_hub.CurrentSession?.Id != session.Id || _hub.CurrentSession.Phase != SessionPhase.GameplayInitialized)
                return Failure(RecipeCatalogStatus.SessionUnavailable, session.Id, "Session changed during recipe query.");
            return result;
        }
        catch (RecipeCatalogLimitException)
        {
            return Failure(RecipeCatalogStatus.LimitExceeded, session.Id, "Recipe catalog exceeds supported bounds.");
        }
        catch (Exception exception)
        {
            try { _report(exception); } catch { }
            return Failure(RecipeCatalogStatus.NativeFailure, session.Id, "Recipe catalog could not be read.");
        }
    }
    internal static RecipeCatalogSnapshot Failure(RecipeCatalogStatus status, Guid? session, string detail) =>
        new(status, session, detail, Array.Empty<RecipeSnapshot>());
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true;
        if (Availability.IsAvailable) _hub.SetCapability("recipe-catalog", false, "Recipe service stopped.", ServiceUnavailableReason.ApiStopped);
    }
}
