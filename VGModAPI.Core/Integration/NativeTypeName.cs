using System;
using System.Linq;
using System.Text;

namespace VGModAPI.Runtime;

/// <summary>
/// One canonical spelling for the native type names the binding catalogs declare.
/// <para>
/// The catalogs are written in the metadata spelling used by the inspected assembly and by the
/// installed-metadata tests: no assembly qualification, backtick arity on the generic definition and
/// angle brackets around the type arguments, for example
/// <c>System.Nullable`1&lt;UnityEngine.Vector2&gt;</c>. Reflection's <see cref="Type.FullName"/>
/// spells a CONSTRUCTED generic differently
/// (<c>System.Nullable`1[[UnityEngine.Vector2, UnityEngine.CoreModule, Version=…]]</c>), so a raw
/// FullName comparison can never match such a binding. That mismatch silently disabled the whole
/// native travel group at runtime (qa-77: MissingMethodException on
/// <c>TravelManager.CancelTravel</c>) while the Cecil-only metadata tests kept passing.
/// </para>
/// <para>
/// Only the shape is normalised — namespace, arity, argument types, array rank, by-ref and pointer
/// decoration are all preserved — so overload discrimination is not weakened in any way.
/// </para>
/// </summary>
internal static class NativeTypeName
{
    internal static string Canonical(Type type)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        if (type.IsByRef) return Canonical(type.GetElementType()!) + "&";
        if (type.IsPointer) return Canonical(type.GetElementType()!) + "*";
        if (type.IsArray)
        {
            int rank = type.GetArrayRank();
            return Canonical(type.GetElementType()!) + "[" + new string(',', rank - 1) + "]";
        }
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var text = new StringBuilder(Canonical(type.GetGenericTypeDefinition())).Append('<');
            text.Append(string.Join(",", type.GetGenericArguments().Select(Canonical)));
            return text.Append('>').ToString();
        }
        // Generic parameters have no FullName; their Name can never match a catalog entry, which is
        // the correct outcome (a binding must never resolve to an open generic member).
        return type.FullName ?? type.Name;
    }

    /// <summary>True when the reflected type is exactly the declared native type.</summary>
    internal static bool Matches(Type type, string declared) => Canonical(type) == declared;
}
