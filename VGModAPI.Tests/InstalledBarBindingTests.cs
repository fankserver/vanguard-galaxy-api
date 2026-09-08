using System;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledBarBindingTests
{
    [Fact]
    public void NativeRosterAndInertContactFieldsMatchTheInspectedContract()
    {
        string path = Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
            ?? throw new InvalidOperationException("Run make check-bindings against the installed game.");
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var module = assembly.MainModule;
        var station = module.GetType("Source.Galaxy.POI.SpaceStation");
        var bar = module.GetType("Source.Galaxy.POI.Station.Bar");
        var patron = module.GetType("Source.Galaxy.POI.Station.BarPatron");
        var salesman = module.GetType("Source.Galaxy.POI.Station.Patrons.Salesman");
        Assert.Equal(bar.FullName, station.Fields.Single(field => field.Name == "bar").FieldType.FullName);
        Assert.Equal("System.Collections.Generic.List`1<" + patron.FullName + ">", bar.Fields.Single(field => field.Name == "availablePatrons").FieldType.FullName);
        Assert.Equal("System.Int32", patron.Fields.Single(field => field.Name == "seat").FieldType.FullName);
        Assert.Equal("System.Boolean", patron.Fields.Single(field => field.Name == "initialized").FieldType.FullName);
        Assert.Equal(patron.FullName, salesman.BaseType.FullName);
        foreach (string name in new[] { "_name", "description", "_seed" })
            Assert.Equal("System.String", salesman.Fields.Single(field => field.Name == name).FieldType.FullName);
        Assert.Equal("UnityEngine.Sprite", salesman.Fields.Single(field => field.Name == "_icon").FieldType.FullName);
        Assert.Equal("System.Boolean", salesman.Fields.Single(field => field.Name == "_isMale").FieldType.FullName);
        var constructor = salesman.Methods.Single(method => method.IsConstructor && method.Parameters.Count == 2);
        Assert.Equal("System.String", constructor.Parameters[0].ParameterType.FullName);
        Assert.Equal(station.FullName, constructor.Parameters[1].ParameterType.FullName);
        Assert.DoesNotContain(constructor.Body.Instructions, instruction => instruction.Operand is MethodReference method && method.Name == "InitializeData");
        Assert.Equal("LightJson.JsonValue", bar.Methods.Single(method => method.Name == "ToJson").ReturnType.FullName);
        Assert.Equal("System.Int64", bar.Fields.Single(field => field.Name == "lastUpdateTime").FieldType.FullName);
        Assert.Equal("System.String", bar.Fields.Single(field => field.Name == "nextUpdateSeed").FieldType.FullName);
    }
}
