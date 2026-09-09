using System;
using System.Collections.Generic;

namespace VGModAPI.Core;
internal static class OwnedItemDeclarationPublication
{
    // Optional declarations must not abort native catalog loading. Actual saved-item lookup remains strict.
    internal static void Publish(IEnumerable<OwnedItemIdentity> definitions, Action<string> ensure, Action<string, Exception> report)
    {
        foreach (var definition in definitions)
        {
            try { ensure(definition.NativeId); }
            catch (Exception error) { try { report(definition.Owner, error); } catch { } }
        }
    }
}
