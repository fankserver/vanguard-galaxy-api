using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class GameBindings
{
    internal readonly Assembly Assembly;
    internal readonly FieldInfo Player;
    internal readonly FieldInfo Ephemeral;
    internal readonly FieldInfo SaveFile;
    internal readonly FieldInfo SavesPath;
    internal readonly FieldInfo Initialized;
    internal GameBindings(Assembly assembly)
    {
        Assembly = assembly;
        Player = Field(BindingCatalog.Player, "current", BindingCatalog.Player, true);
        Ephemeral = Field(BindingCatalog.Player, "isEphemeral", "System.Boolean", false);
        SaveFile = Field(BindingCatalog.File, "File", "System.IO.FileInfo", false);
        SavesPath = Field(BindingCatalog.Save, "SavesPath", "System.String", true);
        Initialized = Field("GameplayManager", "_initialized", "System.Boolean", false);
    }
    internal object? CurrentPlayer => Player.GetValue(null);
    private FieldInfo Field(string type, string name, string fieldType, bool isStatic)
    {
        var result = Assembly.GetType(type, true)!.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (result == null || result.FieldType.FullName != fieldType || result.IsStatic != isStatic)
            throw new MissingFieldException(type, name);
        return result;
    }
    internal Dictionary<string, MethodInfo> Resolve(IEnumerable<MethodBinding> bindings)
    {
        var result = new Dictionary<string, MethodInfo>();
        foreach (var b in bindings)
        {
            var target = Assembly.GetType(b.Type, true)!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SingleOrDefault(m => m.Name == b.Name && m.IsStatic == b.Static && m.ReturnType.FullName == b.ReturnType
                    && m.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(b.Parameters));
            result[b.Key] = target ?? throw new MissingMethodException(b.Type, b.Name);
        }
        return result;
    }
}
