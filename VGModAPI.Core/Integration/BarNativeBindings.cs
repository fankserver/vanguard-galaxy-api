using System;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>Inspected native field shapes and deterministic contact presentation, without installation.</summary>
internal sealed class BarNativeBindings
{
    internal BarNativeContacts Contacts { get; }
    internal BarNativeWorld World { get; }
    internal BarNativeSerialization Serialization { get; }

    internal BarNativeBindings(Assembly assembly, Func<string, object?> loadNpcPortrait, Action<Exception> report)
    {
        Type Type(string name) => assembly.GetType(name, true)!;
        var station = Type("Source.Galaxy.POI.SpaceStation");
        var bar = Type("Source.Galaxy.POI.Station.Bar");
        var patron = Type("Source.Galaxy.POI.Station.BarPatron");
        var salesman = Type("Source.Galaxy.POI.Station.Patrons.Salesman");
        var icon = Type("Behaviour.Crew.OfficerIcon");
        var icons = Type("Behaviour.Crew.OfficerIcons");
        var getIcon = icons.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException("OfficerIcons.Get");
        var sprite = icon.GetField("sprite", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("OfficerIcon.sprite");
        if (getIcon.ReturnType != icon || sprite.FieldType.FullName != "UnityEngine.Sprite")
            throw new InvalidOperationException("Unsupported officer portrait binding.");
        var characters = Type("Source.Dialogues.Characters");
        var character = Type("Source.Dialogues.Character");
        var lookup = characters.GetMethod("GetCharacter", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException("Characters.GetCharacter");
        var characterSprite = character.GetField("portretSprite", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("Character.portretSprite");
        if (lookup.ReturnType != character || characterSprite.FieldType != sprite.FieldType)
            throw new InvalidOperationException("Unsupported character portrait binding.");
        var portraits = new BarPortraitResolver(loadNpcPortrait, name =>
        {
            var source = lookup.Invoke(null, new object[] { name });
            return source == null ? null : characterSprite.GetValue(source);
        }, sprite.FieldType.IsInstanceOfType, report);
        object? Portrait(BarPatronState state)
        {
            if (state.Portrait != null) return portraits.Resolve(state.Portrait);
            // The inspected Salesman recovery path uses this existing male portrait. Do not
            // consume global RNG or initialize a random sale just to obtain an icon.
            try
            {
                var value = getIcon.Invoke(null, new object[] { "Man02" });
                return value == null ? null : sprite.GetValue(value);
            }
            catch (TargetInvocationException) { return null; } // The scene's icon catalog can be unavailable.
        }
        Contacts = new BarNativeContacts(salesman, patron, station, Portrait);
        World = new BarNativeWorld(station, bar, patron,
            new BarStationSource(Type("Source.Player.GamePlayer"), station), Contacts.IsOwned, Contacts.Create, 5);
        Serialization = new BarNativeSerialization(bar, patron, Type("LightJson.JsonValue"),
            Type("LightJson.JsonObject"), Type("LightJson.JsonArray"), Contacts, World);
    }
}
