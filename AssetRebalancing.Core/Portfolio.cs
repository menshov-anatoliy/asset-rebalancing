namespace AssetRebalancing.Core;

/// <summary>Портфель: целевые и текущие веса активов.</summary>
public sealed class Portfolio
{
    /// <summary>Целевые веса, рассчитанные алгоритмом HRP (сумма = 1).</summary>
    public required IReadOnlyDictionary<Asset, decimal> TargetWeights { get; init; }

    /// <summary>Текущие рыночные веса позиций.</summary>
    public required IReadOnlyDictionary<Asset, decimal> CurrentWeights { get; init; }
}
