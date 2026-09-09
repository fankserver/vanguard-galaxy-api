using System;
using System.IO;
using System.Text;

namespace VGModAPI.Core;

internal sealed class OwnedRecipeIdentity
{
    internal const string Prefix = "vgmodapi.recipe.v1.";
    internal string Owner { get; }
    internal string LocalId { get; }
    internal int Revision { get; }
    internal string Name { get; }
    internal int Credits { get; }
    internal float Seconds { get; }
    internal (string Id, int Count)[] Inputs { get; }
    internal (string Id, int Count) Output { get; }
    internal string NativeId { get; }
    internal OwnedRecipeIdentity(string owner, string local, int revision, string name, int credits, float seconds,
        (string Id, int Count)[] inputs, (string Id, int Count) output)
    {
        ValidateName(owner); ValidateName(local);
        if (revision < 1 || string.IsNullOrWhiteSpace(name) || name.Length > 128 || credits < 0 || credits > 100000000 ||
            float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0 || seconds > 86400 || inputs == null || inputs.Length > 8)
            throw new ArgumentException("Unsupported owned recipe definition.");
        Owner = owner; LocalId = local; Revision = revision; Name = name; Credits = credits; Seconds = seconds;
        Inputs = ((string Id, int Count)[])inputs.Clone(); Output = output;
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true), true))
        {
            writer.Write(owner); writer.Write(local); writer.Write(revision); writer.Write(name); writer.Write(credits); writer.Write(seconds);
            writer.Write(inputs.Length);
            foreach (var input in Inputs) WriteRow(writer, input);
            WriteRow(writer, output);
        }
        // Standard base64 cannot accidentally contain vanilla's special _SubRecipe_ separator.
        NativeId = Prefix + Convert.ToBase64String(bytes.ToArray());
        if (NativeId.Length > 65536) throw new ArgumentException("Owned recipe identity exceeds limit.");
    }
    private static void ValidateName(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value == "." || value == "..") throw new ArgumentException("Invalid owner/local identity.");
        foreach (var c in value)
            if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '.' || c == '_' || c == '-'))
                throw new ArgumentException("Invalid owner/local identity.");
    }
    private static void WriteRow(BinaryWriter writer, (string Id, int Count) row)
    {
        if (string.IsNullOrWhiteSpace(row.Id) || row.Count < 1 || row.Count > 10000) throw new ArgumentException("Invalid recipe item row.");
        if (OwnedItemIdentity.IsReserved(row.Id)) _ = OwnedItemIdentity.Read(row.Id);
        else if (row.Id.Length > 500 || row.Id.StartsWith("vgmodapi.", StringComparison.Ordinal)) throw new ArgumentException("Invalid vanilla item identity.");
        writer.Write(row.Id); writer.Write(row.Count);
    }
    internal static bool IsReserved(string id) => id.StartsWith("vgmodapi.recipe.", StringComparison.Ordinal);
    internal static OwnedRecipeIdentity Read(string id)
    {
        if (id == null || id.Length > 65536 || !id.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unsupported recipe identity.");
        try
        {
            using var bytes = new MemoryStream(Convert.FromBase64String(id.Substring(Prefix.Length)), false);
            using var reader = new BinaryReader(bytes, new UTF8Encoding(false, true));
            string owner = reader.ReadString(), local = reader.ReadString(); int revision = reader.ReadInt32();
            string name = reader.ReadString(); int credits = reader.ReadInt32(); float seconds = reader.ReadSingle();
            int count = reader.ReadInt32(); if (count < 0 || count > 8) throw new InvalidDataException("Recipe ingredient limit.");
            var inputs = new (string, int)[count]; for (int i = 0; i < count; i++) inputs[i] = (reader.ReadString(), reader.ReadInt32());
            var result = new OwnedRecipeIdentity(owner, local, revision, name, credits, seconds, inputs, (reader.ReadString(), reader.ReadInt32()));
            if (bytes.Position != bytes.Length || result.NativeId != id) throw new InvalidDataException("Noncanonical recipe identity.");
            return result;
        }
        catch (Exception error) when (error is not InvalidDataException) { throw new InvalidDataException("Invalid owned recipe identity.", error); }
    }
}
