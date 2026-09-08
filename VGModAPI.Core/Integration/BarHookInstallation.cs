using System;
using System.Collections.Generic;

namespace VGModAPI.Core.Integration;

internal static class BarHookInstallation
{
    // Publish a host only after this transaction returns. No managed contacts may be created
    // while installation is partial; rollback therefore cannot expose an owned contact.
    internal static void Install(IEnumerable<Action> patches, Action rollback)
    {
        try { foreach (var patch in patches) patch(); }
        catch
        {
            rollback();
            throw;
        }
    }
}
