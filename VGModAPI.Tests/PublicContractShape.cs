using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mono.Cecil;

namespace VGModAPI.Tests;

internal static class PublicContractShape
{
    internal static string[] Read(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var rows = new List<string>();
        foreach (var type in assembly.MainModule.GetTypes().Where(IsPublic))
        {
            var name = type.FullName;
            rows.Add($"T\t{name}\t{(type.IsInterface ? "interface" : "type")}\t{type.IsAbstract}\t{type.IsSealed}\t{type.BaseType?.FullName}");
            foreach (var parent in type.Interfaces) rows.Add($"I\t{name}\t{parent.InterfaceType.FullName}");
            foreach (var parameter in type.GenericParameters) rows.Add($"G\t{name}\t{Generic(parameter)}");
            foreach (var field in type.Fields.Where(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly))
                rows.Add($"F\t{name}\t{field.FieldType.FullName} {field.Name}\t{field.Attributes}\t{(field.HasConstant ? Constant(field.Constant) : "")}");
            foreach (var method in type.Methods.Where(m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly))
            {
                rows.Add($"M\t{name}\t{method.FullName}\t{method.Attributes}\t" +
                    string.Join(";", method.Parameters.Select(p => $"{p.Name}:{p.Attributes}:{(p.HasConstant ? Constant(p.Constant) : "")}")) +
                    "\t" + string.Join(";", method.GenericParameters.Select(Generic)));
            }
            foreach (var property in type.Properties.Where(p => Exposed(p.GetMethod) || Exposed(p.SetMethod)))
                rows.Add($"P\t{name}\t{property.FullName}\t{Exposed(property.GetMethod)}\t{Exposed(property.SetMethod)}");
            foreach (var notification in type.Events.Where(e => Exposed(e.AddMethod) || Exposed(e.RemoveMethod)))
                rows.Add($"E\t{name}\t{notification.FullName}");
        }
        return rows.Select(row => row.TrimEnd('\t')).OrderBy(row => row, StringComparer.Ordinal).ToArray();
    }

    private static bool IsPublic(TypeDefinition type) => type.IsPublic || (type.IsNestedPublic && IsPublic(type.DeclaringType));
    private static bool Exposed(MethodDefinition? method) => method != null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);
    private static string Generic(GenericParameter parameter) => parameter.Name + ":" + parameter.Attributes + ":" +
        string.Join(",", parameter.Constraints.Select(c => c.ConstraintType.FullName).OrderBy(c => c, StringComparer.Ordinal));
    private static string Constant(object? value) => value == null ? "null" :
        (value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture))
            .Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}
