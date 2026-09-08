using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed partial class BarContentService
{
    private readonly HashSet<BarPatronId> _interacting = new();

    // Integration must first verify that the clicked native object belongs to this applied plan
    // and is still in the exact current station roster. This guard owns provider/session admission.
    internal bool Interact(BarRosterPlan plan, BarPatronState state)
    {
        _checkThread();
        if (!plan.Patrons.Any(row => ReferenceEquals(row, state)) || !IsCurrent(plan)
            || !_leases.TryGetValue(state.Id.Provider, out var lease) || Guard(lease, plan.Session) != null
            || !ReferenceEquals(plan.Revision, _revision)
            || !lease.Interactions.TryGetValue(state.Id.LocalId, out var action) || !_interacting.Add(state.Id)) return false;
        try
        {
            action(new BarInteraction(plan.Session, state.Id, state.Station));
            return true;
        }
        catch { return false; }
        finally { _interacting.Remove(state.Id); }
    }
}
