using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonAttachmentIndexTests
{
    [Fact]
    public void DuplicateSavedMarkersBlockBothCopiesUntilSessionReset()
    {
        var index = new DungeonAttachmentIndex(); var first = new object(); var second = new object(); var id = Guid.NewGuid();
        Assert.True(index.Bind(first, id)); Assert.True(index.Bind(first, id)); Assert.Same(first, index.Resolve(id));
        Assert.False(index.Bind(second, id)); Assert.Null(index.Find(first)); Assert.Null(index.Find(second)); Assert.Null(index.Resolve(id));
        index.Clear(); Assert.True(index.Bind(second, id)); Assert.Same(second, index.Resolve(id)); Assert.Null(index.Find(first));
    }
    [Fact]
    public void LocationCannotBeReassignedToAnotherOccurrence()
    {
        var index = new DungeonAttachmentIndex(); var location = new object(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        Assert.True(index.Bind(location, first)); Assert.False(index.Bind(location, second));
        Assert.Equal(first, index.Find(location)); Assert.Null(index.Resolve(second));
    }
}
