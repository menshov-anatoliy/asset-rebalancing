namespace AssetRebalancing.Core;

/// <summary>Класс активов.</summary>
public enum AssetClass
{
    Equity,
    Bond,
    Commodity,
    Crypto,
    Cash,
    RealEstate
}

/// <summary>Финансовый актив (тикер + мета-информация).</summary>
public sealed record Asset(string Ticker, string Name, AssetClass Class);
