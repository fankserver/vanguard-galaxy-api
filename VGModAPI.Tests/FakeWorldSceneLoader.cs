using System;
using System.Threading.Tasks;

namespace Behaviour.Util
{
    public class PersistentSingleton<T> : UnityEngine.Object where T : class
    {
        protected static T? instance;
        public static void SetInstance(T? value) => instance = value;
    }
}
namespace UnityEngine { public sealed class Transform : Object { } }
namespace Behaviour
{
    public sealed class GameManager : Behaviour.Util.PersistentSingleton<GameManager>
    {
        public UnityEngine.Transform itemBuilderRoot { get; set; } = new();
    }
}
namespace Behaviour.Bootstrap
{
    public sealed class SceneLoader : Behaviour.Util.PersistentSingleton<SceneLoader>
    {
        public Func<string, Task> Unload = _ => Task.CompletedTask;
        public Task UnloadScene(string sceneName) => Unload(sceneName);
    }
}
