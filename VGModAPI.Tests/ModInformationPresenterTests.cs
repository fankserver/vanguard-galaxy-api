using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModInformationPresenterTests
{
    private sealed class Catalog : IModInformationCatalog
    {
        public IReadOnlyList<ModInformation> Snapshot { get; set; } = Array.Empty<ModInformation>();
        internal bool Fail;
        internal int Refreshes;
        public void Refresh() { ++Refreshes; if (Fail) throw new InvalidOperationException("Private path"); }
    }
    private static ModInformation Row(string id, string? url = null) => new(id, "Mod " + id, new Version(1, 2),
        Array.Empty<ModDependencyInformation>(), url == null ? null : new ModAuthorMetadata(null, null, url, null, "stable"), ModMetadataStatus.Missing);

    [Fact]
    public void EmptyAndLargeSnapshotsSelectOnlyExistingIdsAndPreserveSelection()
    {
        var catalog = new Catalog(); var presenter = new ModInformationPresenter(catalog);
        presenter.Open(); Assert.Empty(presenter.Rows); Assert.Null(presenter.Selected);
        catalog.Snapshot = Enumerable.Range(0, 4096).Select(n => Row(n.ToString())).ToArray();
        presenter.Open(); Assert.Equal(4096, presenter.Rows.Count); Assert.Equal("0", presenter.SelectedId);
        Assert.True(presenter.Select("2048")); Assert.False(presenter.Select("foreign")); presenter.Open(); Assert.Equal("2048", presenter.SelectedId);
        catalog.Snapshot = new[] { Row("new") }; presenter.Open(); Assert.Equal("new", presenter.SelectedId);
    }

    [Fact]
    public void RefreshFailureIsNotPresentedAsFreshOrHealthy()
    {
        var catalog = new Catalog { Snapshot = new[] { Row("a") }, Fail = true }; var presenter = new ModInformationPresenter(catalog);
        presenter.Open(); Assert.Contains("previous snapshot", presenter.RefreshWarning); Assert.DoesNotContain("Private", presenter.RefreshWarning);
        Assert.Single(presenter.Rows); catalog.Fail = false; presenter.Open(); Assert.Null(presenter.RefreshWarning);
    }

    [Fact]
    public void LinksOpenOnlyOnExplicitActionAndUseIndependentAsciiHost()
    {
        var catalog = new Catalog { Snapshot = new[] { Row("a", "https://github.com/example/repo") } }; var presenter = new ModInformationPresenter(catalog);
        var opened = new List<string>(); presenter.Open();
        Assert.True(presenter.TryProjectDestination(out var host)); Assert.Equal("github.com", host); Assert.Empty(opened);
        Assert.True(presenter.OpenProject(opened.Add)); Assert.Equal("https://github.com/example/repo", Assert.Single(opened));
        catalog.Snapshot = new[] { Row("b", "https://127.0.0.1/private") }; presenter.Open();
        Assert.False(presenter.TryProjectDestination(out _)); Assert.False(presenter.OpenProject(opened.Add)); Assert.Single(opened);
    }

    [Fact]
    public void DisplayRemovesDirectionSpoofingButPreservesRtlLettersAndJoining()
    {
        Assert.Equal("שלוםabc", ModInformationPresenter.PlainText("שלום\u202eabc\u202c", 160, false));
        Assert.Equal("a\u200cb", ModInformationPresenter.PlainText("a\u200cb", 160, false));
        Assert.Equal("a b", ModInformationPresenter.PlainText("a\nb", 160, false));
        Assert.Equal("a\nb", ModInformationPresenter.PlainText("a\nb", 160, true));
        Assert.Equal("…", ModInformationPresenter.PlainText("🚀long", 2, false));
        Assert.Equal("<b>literal</b>", ModInformationPresenter.PlainText("<b>literal</b>", 160, false)); // View must disable rich text.
    }

    [Fact]
    public void MissingUpdateMetadataIsNotABrokenModWarning()
    {
        Assert.Equal("No update source.", ModInformationPresenter.UpdateNotice(Row("a")));
        Assert.Equal("No optional author metadata.", ModInformationPresenter.MetadataNotice(Row("a")));
    }
}
