using System;
using System.Threading.Tasks;

namespace VGModAPI.Core.Integration;

/// <summary>Observes the original async operation; only its still-current ticket may publish completion.</summary>
internal static class WorldTravelAsyncCompletion
{
    // publish must be a callback-free native field update; it cannot yield or authorize another operation.
    internal static async Task<bool> Run(WorldTravelScopes scopes, WorldTravelScopes.Leg leg,
        Action verifyNative, Func<Task> operation, Action publish)
    {
        try
        {
            verifyNative();
            var ticket = scopes.BeginAsync(leg);
            await operation(); // Preserve Unity's captured synchronization context and observe failures even after replacement.
            if (!scopes.IsCurrent(ticket)) return false;
            verifyNative();
            if (!scopes.ClaimCompletion(ticket)) return false;
            publish();
            return true;
        }
        catch { scopes.Cancel(leg); throw; }
    }
}
