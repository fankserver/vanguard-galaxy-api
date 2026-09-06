using System;
using System.IO;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class MissionIdentitySnapshotTests
{
    private static string Fingerprint(char value) => new(value, 64);
    [Fact]
    public void RoundtripIsDeterministicAndMatchesUniqueContentsNotOrdinal()
    {
        var a = new MissionIdentityRecord(Fingerprint('a'), Guid.NewGuid());
        var b = new MissionIdentityRecord(Fingerprint('b'), Guid.NewGuid());
        var bytes = MissionIdentitySnapshot.Encode(new[] { a, b });
        Assert.Equal(bytes, MissionIdentitySnapshot.Encode(new[] { b, a }));
        var restored = MissionIdentitySnapshot.Decode(bytes);
        Assert.Equal(new Guid?[] { b.InstanceId, a.InstanceId, null }, MissionIdentitySnapshot.MatchUnique(restored, new[] { b.Fingerprint, a.Fingerprint, Fingerprint('c') }));
    }
    [Fact]
    public void DuplicateSavedOrCurrentContentsNeverGuessIdentity()
    {
        var first = new MissionIdentityRecord(Fingerprint('a'), Guid.NewGuid());
        var second = new MissionIdentityRecord(Fingerprint('a'), Guid.NewGuid());
        Assert.Null(Assert.Single(MissionIdentitySnapshot.MatchUnique(new[] { first, second }, new[] { first.Fingerprint })));
        Assert.All(MissionIdentitySnapshot.MatchUnique(new[] { first }, new[] { first.Fingerprint, first.Fingerprint }), id => Assert.Null(id));
    }
    [Fact]
    public void RepeatedOccurrenceIdsAreRejectedEvenWithDifferentContents()
    {
        var id = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() => MissionIdentitySnapshot.Encode(new[] {
            new MissionIdentityRecord(Fingerprint('a'), id), new MissionIdentityRecord(Fingerprint('b'), id) }));
    }
    [Fact]
    public void EveryTruncationAndTrailingDataAreRejected()
    {
        var bytes = MissionIdentitySnapshot.Encode(new[] { new MissionIdentityRecord(Fingerprint('f'), Guid.NewGuid()) });
        for (int length = 0; length < bytes.Length; length++)
            Assert.Throws<InvalidDataException>(() => MissionIdentitySnapshot.Decode(bytes.Take(length).ToArray()));
        Assert.Throws<InvalidDataException>(() => MissionIdentitySnapshot.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
        bytes[3] = (byte)'2'; Assert.Throws<InvalidDataException>(() => MissionIdentitySnapshot.Decode(bytes));
    }
    [Fact]
    public void MalformedIdentityAndCapacityAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new MissionIdentityRecord(Fingerprint('A'), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new MissionIdentityRecord(Fingerprint('a'), Guid.Empty));
        var records = Enumerable.Range(0, MissionIdentitySnapshot.MaxEntries + 1).Select(_ => new MissionIdentityRecord(Fingerprint('a'), Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(() => MissionIdentitySnapshot.Encode(records));
        Assert.Throws<InvalidDataException>(() => MissionIdentitySnapshot.Decode(new byte[MissionIdentitySnapshot.MaxBytes + 1]));
    }
    [Fact]
    public void EmptySavedHistoryDoesNotInventCorrespondence()
    {
        var saved = MissionIdentitySnapshot.Decode(MissionIdentitySnapshot.Encode(Array.Empty<MissionIdentityRecord>()));
        Assert.Empty(saved);
        Assert.Null(Assert.Single(MissionIdentitySnapshot.MatchUnique(saved, new[] { Fingerprint('a') })));
    }
}
