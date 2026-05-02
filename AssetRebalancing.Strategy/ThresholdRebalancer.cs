using AssetRebalancing.Core;

namespace AssetRebalancing.Strategy;

/// <summary>Команда на ребалансировку одного актива.</summary>
public sealed record RebalanceOrder(
    Asset Asset,
    decimal CurrentWeight,
    decimal TargetWeight,
    decimal Delta);

/// <summary>
/// Ребалансировщик на основе порогов отклонения (threshold rebalancing).
///
/// Срабатывает, когда вес актива отклонился от целевого более чем на
/// <see cref="AbsThreshold"/> (абсолютных пп.) ИЛИ на
/// <see cref="RelThreshold"/> (относительных процентов от целевого).
/// </summary>
public sealed class ThresholdRebalancer
{
    /// <summary>Абсолютный порог отклонения (по умолчанию 5 п.п.).</summary>
    public decimal AbsThreshold { get; init; } = 0.05m;

    /// <summary>Относительный порог отклонения (по умолчанию 20%).</summary>
    public decimal RelThreshold { get; init; } = 0.20m;

    /// <summary>
    /// Возвращает список приказов на ребалансировку для позиций,
    /// вышедших за порог отклонения.
    /// </summary>
    public IReadOnlyList<RebalanceOrder> Plan(Portfolio portfolio)
    {
        var orders = new List<RebalanceOrder>();

        foreach (var (asset, target) in portfolio.TargetWeights)
        {
            var current = portfolio.CurrentWeights.GetValueOrDefault(asset, 0m);
            var absDelta = Math.Abs(current - target);
            var relDelta = target > 0m ? absDelta / target : 1m;

            if (absDelta >= AbsThreshold || relDelta >= RelThreshold)
                orders.Add(new RebalanceOrder(asset, current, target, target - current));
        }

        return orders.AsReadOnly();
    }
}
