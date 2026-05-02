using AssetRebalancing.Core;
using MathNet.Numerics.Statistics;

namespace AssetRebalancing.Analytics;

/// <summary>
/// Вычисляет матрицу корреляций Пирсона на основе логарифмических доходностей.
/// </summary>
public sealed class CorrelationAnalyzer
{
    /// <summary>
    /// Строит матрицу корреляций Пирсона по логарифмическим доходностям.
    /// </summary>
    /// <param name="prices">Словарь «актив → список котировок» (одинаковые даты не требуются).</param>
    /// <returns>Массив имён активов и соответствующая матрица корреляций n×n.</returns>
    public (string[] Tickers, double[,] Matrix) BuildCorrelationMatrix(
        IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> prices)
    {
        var tickers = prices.Keys.OrderBy(k => k).ToArray();
        var returns = tickers.ToDictionary(
            t => t,
            t => LogReturns(prices[t]).ToArray());

        int n = tickers.Length;
        var matrix = new double[n, n];

        for (int i = 0; i < n; i++)
        {
            matrix[i, i] = 1.0;
            for (int j = i + 1; j < n; j++)
            {
                // Выравниваем по минимальной длине (упрощение для демо).
                int len = Math.Min(returns[tickers[i]].Length, returns[tickers[j]].Length);
                var xi = returns[tickers[i]].Take(len).ToArray();
                var xj = returns[tickers[j]].Take(len).ToArray();

                double rho = len < 2 ? 0.0 : Correlation.Pearson(xi, xj);
                matrix[i, j] = rho;
                matrix[j, i] = rho;
            }
        }

        return (tickers, matrix);
    }

    // ── private helpers ────────────────────────────────────────────────────

    private static IEnumerable<double> LogReturns(IReadOnlyList<PriceBar> bars)
    {
        for (int i = 1; i < bars.Count; i++)
        {
            var prev = (double)bars[i - 1].Close;
            var curr = (double)bars[i].Close;
            if (prev > 0)
                yield return Math.Log(curr / prev);
        }
    }
}
