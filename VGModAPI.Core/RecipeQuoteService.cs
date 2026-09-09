using System;

namespace VGModAPI.Core;

internal interface IRecipeQuoteSource
{
    void Invalidate();
    RecipeStationHandle? CurrentStation(Guid sessionId);
    RecipeQuote Quote(RecipeStationHandle station, RecipeId recipe, int batches, RefineryInputPolicy policy, long revision);
}

internal sealed class RecipeQuoteService : IRecipeQuoteService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IRecipeQuoteSource? _source;
    private readonly IServiceStatus _status;
    private readonly Action<Exception> _report;
    private readonly IDisposable _lifetime;
    private bool _disposed;
    private long _revision;
    internal RecipeQuoteService(LifecycleHub hub, IRecipeQuoteSource? source, Action<Exception> report)
    {
        _hub = hub; _source = source; _report = report;
        _status = hub.Services.Get("recipe-quotes");
        if (source == null && _status.Availability.IsAvailable)
            hub.SetCapability("recipe-quotes", false, "Quote bindings unavailable.");
        _lifetime = hub.Subscribe("vgmodapi.recipe-quotes", message =>
        {
            if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
                _source?.Invalidate();
        });
    }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public RecipeStationHandle? CurrentStation
    {
        get
        {
            _hub.CheckThread(); var session = _hub.CurrentSession;
            if (_disposed || _source == null || !Availability.IsAvailable || session?.Phase != SessionPhase.GameplayInitialized) return null;
            try
            {
                var station = _source.CurrentStation(session.Id);
                return Availability.IsAvailable && _hub.CurrentSession?.Id == session.Id && _hub.CurrentSession.Phase == SessionPhase.GameplayInitialized ? station : null;
            }
            catch (Exception exception) { Report(exception); return null; }
        }
    }
    public RecipeQuote Quote(RecipeStationHandle station, RecipeId recipe, int batches = 1, RefineryInputPolicy refineryPolicy = RefineryInputPolicy.Manual)
    {
        _hub.CheckThread();
        if (station == null) throw new ArgumentNullException(nameof(station));
        if (recipe == null) throw new ArgumentNullException(nameof(recipe));
        var session = _hub.CurrentSession;
        if (_disposed || _source == null || !Availability.IsAvailable)
            return Failure(RecipeQuoteStatus.IntegrationUnavailable, station, recipe, batches, Availability.Detail);
        if (session?.Phase != SessionPhase.GameplayInitialized) return Failure(RecipeQuoteStatus.SessionUnavailable, station, recipe, batches, "Gameplay session required.");
        if (station.SessionId != session.Id) return Failure(RecipeQuoteStatus.StaleHandle, station, recipe, batches, "Station belongs to another session.");
        if (batches < 1 || !Enum.IsDefined(typeof(RefineryInputPolicy), refineryPolicy)) return Failure(RecipeQuoteStatus.InvalidRequest, station, recipe, batches, "Positive batch count and supported policy required.");
        if (refineryPolicy != RefineryInputPolicy.Manual && !recipe.LocalId.StartsWith("refining/", StringComparison.Ordinal))
            return Failure(RecipeQuoteStatus.InvalidRequest, station, recipe, batches, "Automatic selection applies only to refining.");
        try
        {
            var quote = _source.Quote(station, recipe, batches, refineryPolicy, checked(++_revision));
            if (!Availability.IsAvailable) return Failure(RecipeQuoteStatus.IntegrationUnavailable, station, recipe, batches, Availability.Detail);
            if (_hub.CurrentSession?.Id != session.Id || _hub.CurrentSession.Phase != SessionPhase.GameplayInitialized)
                return Failure(RecipeQuoteStatus.StaleHandle, station, recipe, batches, "Session changed during quote.");
            return quote;
        }
        catch (RecipeCatalogLimitException) { return Failure(RecipeQuoteStatus.LimitExceeded, station, recipe, batches, "Quote exceeds supported bounds."); }
        catch (OverflowException) { return Failure(RecipeQuoteStatus.InvalidRequest, station, recipe, batches, "Batch quantities exceed supported arithmetic."); }
        catch (Exception exception) { Report(exception); return Failure(RecipeQuoteStatus.NativeFailure, station, recipe, batches, "Recipe quote could not be read."); }
    }
    public RecipeQuote QuoteMaterialExtraction(RecipeStationHandle station, RecipeResourceId material, int count = 1)
    {
        _hub.CheckThread();
        if (station == null) throw new ArgumentNullException(nameof(station));
        if (material == null) throw new ArgumentNullException(nameof(material));
        if (material.LocalId.Length > 500)
            return Failure(RecipeQuoteStatus.InvalidRequest, station, new RecipeId(material.ProviderId, material.LocalId), count, "Material identity exceeds extraction bounds.");
        var id = new RecipeId(material.ProviderId, "extraction/" + material.LocalId);
        return material.Kind == RecipeResourceKind.RefinedMaterial ? Quote(station, id, count) :
            Failure(RecipeQuoteStatus.InvalidRequest, station, id, count, "Refined material identity required.");
    }
    private void Report(Exception error) { try { _report(error); } catch { } }
    internal static RecipeQuote Failure(RecipeQuoteStatus status, RecipeStationHandle? station, RecipeId recipe, int batches, string detail) =>
        new(status, detail, station, recipe, batches, 0, Array.Empty<RecipeIngredientRequirement>(), Array.Empty<RecipeOutputPreview>(), Array.Empty<RecipeBlocker>());
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true; _lifetime.Dispose();
        if (Availability.IsAvailable) _hub.SetCapability("recipe-quotes", false, "Recipe quotes stopped.", ServiceUnavailableReason.ApiStopped);
        try { _source?.Invalidate(); } catch (Exception exception) { Report(exception); }
    }
}
