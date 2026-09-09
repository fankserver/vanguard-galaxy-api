using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class HudIconResolverTests
{
    private sealed class Icon { internal bool Alive = true; }

    [Fact]
    public void PendingHasNoSpriteOrLabelThenFallsBackAndStillRetries()
    {
        var calls = 0; Icon? available = null;
        var resolver = new HudIconResolver<Icon>(_ => { calls++; return available; }, icon => icon.Alive, _ => { });
        var initial = resolver.Read(HudIcon.Storage, 0);
        Assert.Null(initial.Icon); Assert.Equal(HudIconState.Pending, initial.State);
        resolver.Read(HudIcon.Storage, .5); Assert.Equal(1, calls);
        Assert.Equal(HudIconState.Pending, resolver.Read(HudIcon.Storage, 1).State);
        var fallback = resolver.Read(HudIcon.Storage, 2);
        Assert.Null(fallback.Icon); Assert.Equal(HudIconState.LabelFallback, fallback.State); Assert.Equal(3, calls);
        available = new Icon();
        var late = resolver.Read(HudIcon.Storage, 3);
        Assert.Same(available, late.Icon); Assert.Equal(HudIconState.Resolved, late.State);
        resolver.Read(HudIcon.Storage, 30); Assert.Equal(4, calls);
    }

    [Fact]
    public void ResolutionIsSharedAcrossButtonsAndIndependentBetweenVisuals()
    {
        var calls = new List<HudIcon>(); var storage = new Icon();
        var resolver = new HudIconResolver<Icon>(icon => { calls.Add(icon); return icon == HudIcon.Storage ? storage : null; },
            icon => icon.Alive, _ => { });
        for (var i = 0; i < 16; i++) Assert.Same(storage, resolver.Read(HudIcon.Storage, 0).Icon);
        Assert.Single(calls);
        Assert.Equal(HudIconState.Pending, resolver.Read(HudIcon.Refinery, 0).State);
        Assert.Equal(new[] { HudIcon.Storage, HudIcon.Refinery }, calls);
    }

    [Fact]
    public void DestroyedAssetIsNeverReturnedAndCanBeReplacedWithoutModelUpdate()
    {
        Icon? asset = new Icon();
        var resolver = new HudIconResolver<Icon>(_ => asset, icon => icon.Alive, _ => { });
        Assert.Same(asset, resolver.Read(HudIcon.Refinery, 0).Icon);
        asset.Alive = false;
        var lost = resolver.Read(HudIcon.Refinery, 4);
        Assert.Null(lost.Icon); Assert.Equal(HudIconState.LabelFallback, lost.State);
        asset = new Icon(); Assert.Same(asset, resolver.Read(HudIcon.Refinery, 5).Icon);
        resolver.Clear(); asset = null;
        Assert.Equal(HudIconState.Pending, resolver.Read(HudIcon.Refinery, 10).State);
    }

    [Fact]
    public void ResolutionAndLoggingFailuresAreIsolatedAndDoNotStopLateRecovery()
    {
        var logs = 0; var failing = true; var asset = new Icon();
        var resolver = new HudIconResolver<Icon>(_ => failing ? throw new InvalidOperationException() : asset,
            icon => icon.Alive, _ => { logs++; throw new Exception("logger"); });
        Assert.Equal(HudIconState.Pending, resolver.Read(HudIcon.Storage, 0).State);
        Assert.Equal(HudIconState.LabelFallback, resolver.Read(HudIcon.Storage, 2).State);
        Assert.Equal(1, logs);
        failing = false;
        Assert.Same(asset, resolver.Read(HudIcon.Storage, 3).Icon);
    }

    [Fact]
    public void SemanticMappingsDisambiguateAtlasCellsAndRejectUnknownVisuals()
    {
        Assert.True(HudIconSprites.Matches(HudIcon.Storage, "SkillIcons1_31", 463, 793));
        Assert.False(HudIconSprites.Matches(HudIcon.Storage, "SkillIcons1_31", 0, 0));
        Assert.False(HudIconSprites.Matches(HudIcon.Storage, "Refinery", 463, 793));
        Assert.True(HudIconSprites.Matches(HudIcon.Refinery, "Refinery", 0, 0));
        Assert.False(HudIconSprites.Matches((HudIcon)100, "Refinery", 0, 0));
        var resolver = new HudIconResolver<Icon>(_ => null, _ => true, _ => { });
        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.Read((HudIcon)100, 0));
    }
}
