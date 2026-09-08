namespace VGModAPI.Qualification;

internal static class ModMenuSessionChecks
{
    internal static bool Inactive(SessionSnapshot? session, bool allowHistory) =>
        session == null || allowHistory && session.Phase == SessionPhase.Invalidated;
}
