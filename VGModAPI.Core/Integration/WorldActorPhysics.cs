using System;
using System.Linq;
using System.Reflection;
using System.IO;

namespace VGModAPI.Core.Integration;

/// <summary>Disables physics on the owned object's own components, never traversing unrelated child actors.</summary>
internal sealed class WorldActorPhysics
{
    private readonly Type _body, _collider;
    private readonly PropertyInfo _simulated, _enabled;
    internal WorldActorPhysics(Assembly game) : this(Physics(game).GetType("UnityEngine.Rigidbody2D", true)!, Physics(game).GetType("UnityEngine.Collider2D", true)!) { }
    private static Assembly Physics(Assembly game) => game.GetType("Behaviour.Unit.AbstractUnit", true)!
        .GetMethod("OnCollisionEnter2D", BindingFlags.NonPublic | BindingFlags.Instance)!.GetParameters()[0].ParameterType.Assembly;
    internal WorldActorPhysics(Type body, Type collider)
    {
        _body = body; _collider = collider;
        _simulated = BooleanSetter(body, "simulated"); _enabled = BooleanSetter(collider, "enabled");
    }
    private static PropertyInfo BooleanSetter(Type type, string name)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType != typeof(bool) || property.SetMethod == null || property.SetMethod.IsStatic)
            throw new MissingMemberException(type.FullName, name);
        return property;
    }
    internal void Stop(object actor)
    {
        WorldNativeAssetInspection.RequireAlive(actor);
        var query = actor.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(method =>
            method.Name == "GetComponents" && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 0);
        var bodies = Read(query, actor, _body); var colliders = Read(query, actor, _collider);
        WorldNativeAssetInspection.RequireAlive(actor);
        foreach (var body in bodies) _simulated.SetValue(body, false);
        foreach (var collider in colliders) _enabled.SetValue(collider, false);
    }
    private static Array Read(MethodInfo query, object actor, Type type)
    {
        var values = query.MakeGenericMethod(type).Invoke(actor, null) as Array;
        if (values == null || values.Rank != 1 || values.Length > 64) throw new InvalidDataException("Unsupported owned physics component inventory.");
        foreach (var value in values)
        {
            if (value == null || !type.IsInstanceOfType(value)) throw new InvalidDataException("Invalid owned physics component.");
            WorldNativeAssetInspection.RequireAlive(value);
        }
        return values;
    }
}
