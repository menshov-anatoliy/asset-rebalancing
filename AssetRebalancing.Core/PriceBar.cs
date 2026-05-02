namespace AssetRebalancing.Core;

/// <summary>Котировка актива на конкретную дату (дневная цена закрытия).</summary>
public sealed record PriceBar(DateOnly Date, decimal Close);
