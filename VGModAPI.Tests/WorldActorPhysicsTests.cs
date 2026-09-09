using System.Linq;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldActorPhysicsTests
{
    public sealed class Body : UnityEngine.Object { public bool simulated { get; set; } = true; }
    public sealed class Collider : UnityEngine.Object { public bool enabled { get; set; } = true; }
    public sealed class Node : UnityEngine.Object
    {
        public object[] Components = System.Array.Empty<object>();
        public Node? Child;
        public T[] GetComponents<T>() => Components.OfType<T>().ToArray();
    }
    [Fact]
    public void ShutdownTouchesOnlyTheOwnedRootsPhysics()
    {
        var body = new Body(); var collider = new Collider(); var unrelated = new Body();
        var root = new Node { Components = new object[] { body, collider }, Child = new Node { Components = new object[] { unrelated } } };
        var physics = new WorldActorPhysics(typeof(Body), typeof(Collider));
        physics.Stop(root); physics.Stop(root);
        Assert.False(body.simulated); Assert.False(collider.enabled); Assert.True(unrelated.simulated);
    }
}
