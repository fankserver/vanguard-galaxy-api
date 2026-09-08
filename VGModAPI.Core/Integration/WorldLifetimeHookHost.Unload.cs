using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace VGModAPI.Core.Integration;

internal sealed partial class WorldLifetimeHookHost
{
    public Task<bool>? CaptureSceneUnload(object manager, string sceneName)
    {
        RequireSceneTransition(manager);
        var leg = Travel.CaptureExecuting();
        if (leg == null) return null; // Keep the untracked vanilla async-void implementation.
        var assembly = _localTarget.DeclaringType!.Assembly;
        var loaderType = assembly.GetType(BindingCatalog.Scenes, true)!;
        var instance = assembly.GetType("Behaviour.Util.PersistentSingleton`1", true)!.MakeGenericType(loaderType)
            .GetField("instance", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingFieldException("SceneLoader singleton");
        var loader = instance.GetValue(null) ?? throw new InvalidDataException("No native scene loader.");
        var unload = loaderType.GetMethod("UnloadScene", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException("SceneLoader.UnloadScene");
        var loading = _localTarget.DeclaringType.GetField("loadingNextScene", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("TravelManager.loadingNextScene");
        if (unload.ReturnType != typeof(Task) || loading.FieldType != typeof(bool)) throw new InvalidDataException("Unsupported scene unload shape.");
        var objectType = loader.GetType();
        while (objectType != null && objectType.FullName != "UnityEngine.Object") objectType = objectType.BaseType;
        var pointer = objectType?.GetField("m_CachedPtr", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (pointer?.FieldType != typeof(IntPtr)) throw new InvalidDataException("Scene loader native lifetime is unavailable.");
        return WorldTravelAsyncCompletion.Run(Travel, leg, () =>
        {
            VerifyRouteNative(leg.Route, manager);
            Travel.RequireActive(leg, _hub.CurrentSession!.Id, _player.GetValue(null)!, manager);
            if (!ReferenceEquals(_localTarget.GetValue(manager), leg.Target) || !AllowUse(leg.Target) ||
                !ReferenceEquals(instance.GetValue(null), loader) || (IntPtr)pointer.GetValue(loader)! == IntPtr.Zero)
                throw new InvalidDataException("Scene unload lost its original target or live loader.");
        }, () =>
        {
            try { return (Task)(unload.Invoke(loader, new object[] { sceneName }) ?? throw new InvalidDataException("Scene unload returned no task.")); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }, () => loading.SetValue(manager, false));
    }
}
