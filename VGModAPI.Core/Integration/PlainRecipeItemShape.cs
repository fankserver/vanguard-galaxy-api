using System;
using System.Reflection;

namespace VGModAPI.Core.Integration;

internal sealed class PlainRecipeItemShape
{
    private readonly PropertyInfo _category, _itemBuilder, _equipmentBuilder;
    internal PlainRecipeItemShape(Type itemType)
    {
        _category = Property(itemType, "itemCategory");
        _itemBuilder = Property(itemType, "itemBuilder");
        _equipmentBuilder = Property(itemType, "equipmentBuilder");
    }
    private static PropertyInfo Property(Type type, string name) => type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
        ?? throw new MissingMemberException(type.FullName, name);
    internal void Require(object item)
    {
        // Builder provenance controls native serialization and observed resource identity, independently of category.
        if (_itemBuilder.GetValue(item) != null || _equipmentBuilder.GetValue(item) != null)
            throw new InvalidOperationException("Builder-backed recipe items are unsupported.");
        string? category = _category.GetValue(item)?.ToString();
        if (category != "TradeGoods" && category != "RefinedProduct" && category != "Ore" && category != "Salvage" && category != "Junk" && category != "Crystal")
            throw new InvalidOperationException("Recipe item shape is not supported plain goods.");
    }
}
