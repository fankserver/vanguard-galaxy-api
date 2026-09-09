using System;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;
public sealed class PlainRecipeItemShapeTests
{
    private sealed class Item
    {
        public string itemCategory { get; set; } = "TradeGoods";
        public object? itemBuilder { get; set; }
        public object? equipmentBuilder { get; set; }
    }
    [Fact]
    public void AllowedCategoryDoesNotOverrideBuilderProvenance()
    {
        var shape = new PlainRecipeItemShape(typeof(Item)); var item = new Item();
        shape.Require(item);
        item.itemBuilder = new object(); Assert.Throws<InvalidOperationException>(() => shape.Require(item));
        item.itemBuilder = null; item.equipmentBuilder = new object();
        Assert.Throws<InvalidOperationException>(() => shape.Require(item));
        item.equipmentBuilder = null; shape.Require(item);
        item.itemCategory = "Module"; Assert.Throws<InvalidOperationException>(() => shape.Require(item));
    }
}
