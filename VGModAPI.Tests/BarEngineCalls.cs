using System;
using VGModAPI.Core;

namespace VGModAPI.Tests;

/// <summary>Internal engine regressions bypass public scheduling to exercise native admission and session guards.</summary>
internal static class BarEngineCalls
{
    internal static BarResult Place(this IBarProvider provider, Guid session, string local)
        => ((BarContentService.Lease)provider).Place(session, local);
    internal static BarResult Remove(this IBarProvider provider, Guid session, string local)
        => ((BarContentService.Lease)provider).Remove(session, local);
    internal static BarResult Unregister(this IBarProvider provider, string local)
        => ((BarContentService.Lease)provider).Unregister(local);
    internal static BarRegistrationResult RegisterEngine(this IBarProvider provider, BarPatronDefinition definition, Action<BarInteraction> callback)
    {
        var result = provider.Register(definition);
        if (result.Status == BarStatus.Succeeded) ((BarContentService.Lease)provider).Interactions[definition.LocalId] = callback;
        return result;
    }
}
