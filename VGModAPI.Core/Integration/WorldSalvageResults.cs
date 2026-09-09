using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core.Integration;

/// <summary>Reserved constructor results remain owned even if generation fails before publication.</summary>
internal sealed class WorldSalvageResults
{
    internal sealed class Receipt
    {
        internal readonly object Poi, Descriptor;
        internal readonly Func<bool> Admit, Stable;
        internal object? Candidate;
        internal bool Published, Rejected, Checking;
        internal Receipt(object poi, object descriptor, Func<bool> admit, Func<bool> stable)
        { Poi = poi; Descriptor = descriptor; Admit = admit; Stable = stable; }
    }
    private readonly ConditionalWeakTable<object, Receipt> _results = new();
    internal Receipt Begin(object poi, object descriptor, Func<bool> admit, Func<bool> stable)
        => new(poi, descriptor, admit, stable);
    private static void Require(Receipt receipt)
    {
        if (receipt.Rejected || receipt.Checking) { receipt.Rejected = true; throw new InvalidDataException("Rejected or reentrant salvage result."); }
        receipt.Checking = true;
        try
        {
            if (!receipt.Admit() || receipt.Rejected || !receipt.Stable()) throw new InvalidDataException("Salvage result origin is unavailable.");
        }
        catch { receipt.Rejected = true; throw; }
        finally { receipt.Checking = false; }
    }
    internal void Reserve(Receipt receipt, object result)
    {
        bool existing = _results.TryGetValue(result, out _);
        if (!existing) _results.Add(result, receipt);
        if (existing || receipt.Candidate != null)
        { receipt.Rejected = true; throw new InvalidDataException("Ambiguous salvage constructor result."); }
        receipt.Candidate = result;
        Require(receipt);
    }
    internal void Complete(Receipt receipt, object result)
    {
        if (receipt.Published || !ReferenceEquals(receipt.Candidate, result))
        { receipt.Rejected = true; throw new InvalidDataException("Salvage result substitution or repeated completion."); }
        Require(receipt); receipt.Published = true;
    }
    internal void Reject(Receipt receipt) => receipt.Rejected = true;
    internal void RequirePublication(object poi, object result)
    {
        if (!_results.TryGetValue(result, out var receipt)) return;
        if (!receipt.Published || !ReferenceEquals(poi, receipt.Poi)) throw new InvalidDataException("Salvage result lacks publication authority for this POI.");
        Require(receipt);
    }
}
