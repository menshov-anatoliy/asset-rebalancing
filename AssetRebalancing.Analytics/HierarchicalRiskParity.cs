using AssetRebalancing.Core;

namespace AssetRebalancing.Analytics;

/// <summary>
/// Реализация алгоритма Hierarchical Risk Parity (HRP) по статье
/// Marcos López de Prado «Building Diversified Portfolios that Outperform Out-of-Sample» (2016).
///
/// Этапы:
///   1. Преобразование корреляции в расстояние: d_ij = sqrt(0.5 * (1 - ρ_ij))
///   2. Иерархическая кластеризация (single linkage) → дендрограмма
///   3. Quasi-diagonalization: переупорядочивание активов по кластерам
///   4. Recursive bisection: рекурсивное inverse-variance распределение весов
/// </summary>
public sealed class HierarchicalRiskParity
{
    /// <summary>
    /// Вычисляет HRP-веса для заданного набора активов.
    /// </summary>
    /// <param name="tickers">Имена активов (порядок соответствует строкам/столбцам <paramref name="corrMatrix"/>).</param>
    /// <param name="corrMatrix">Матрица корреляций n×n.</param>
    /// <param name="variances">Дисперсии доходностей каждого актива (порядок совпадает с <paramref name="tickers"/>).</param>
    /// <returns>Словарь «тикер → вес» (сумма весов ≈ 1).</returns>
    public IReadOnlyDictionary<string, double> ComputeWeights(
        string[] tickers,
        double[,] corrMatrix,
        double[] variances)
    {
        int n = tickers.Length;
        if (n == 0) return new Dictionary<string, double>();
        if (n == 1) return new Dictionary<string, double> { [tickers[0]] = 1.0 };

        // Шаг 1: матрица расстояний
        var dist = ToDistanceMatrix(corrMatrix, n);

        // Шаг 2: single-linkage иерархическая кластеризация → порядок листьев
        var order = SingleLinkageOrder(dist, n);

        // Шаг 3+4: рекурсивное bisection с inverse-variance весами
        var weights = new double[n];
        for (int i = 0; i < n; i++) weights[i] = 1.0;

        RecursiveBisection(order, variances, weights, 0, order.Length - 1);

        // Нормализация (защита от численных ошибок)
        double sum = weights.Sum();
        return tickers
            .Select((t, i) => (t, w: sum > 0 ? weights[order[i]] / sum : 1.0 / n))
            .ToDictionary(x => x.t, x => x.w);
    }

    // ── Step 1: correlation → distance ────────────────────────────────────

    private static double[,] ToDistanceMatrix(double[,] corr, int n)
    {
        var d = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                d[i, j] = Math.Sqrt(Math.Max(0, 0.5 * (1 - corr[i, j])));
        return d;
    }

    // ── Step 2: single-linkage clustering (returns leaf order) ────────────

    private static int[] SingleLinkageOrder(double[,] dist, int n)
    {
        // Храним кластеры как списки индексов
        var clusters = Enumerable.Range(0, n)
                                 .Select(i => new List<int> { i })
                                 .ToList();

        while (clusters.Count > 1)
        {
            // Найти пару кластеров с минимальным single-linkage расстоянием
            double minDist = double.MaxValue;
            int ci = 0, cj = 1;
            for (int a = 0; a < clusters.Count; a++)
                for (int b = a + 1; b < clusters.Count; b++)
                {
                    double d = SingleLinkageDist(clusters[a], clusters[b], dist);
                    if (d < minDist) { minDist = d; ci = a; cj = b; }
                }

            // Объединяем cj в ci
            clusters[ci].AddRange(clusters[cj]);
            clusters.RemoveAt(cj);
        }

        return clusters[0].ToArray();
    }

    private static double SingleLinkageDist(List<int> a, List<int> b, double[,] dist)
    {
        double min = double.MaxValue;
        foreach (var i in a)
            foreach (var j in b)
                if (dist[i, j] < min) min = dist[i, j];
        return min;
    }

    // ── Step 4: recursive bisection ───────────────────────────────────────

    private static void RecursiveBisection(
        int[] order, double[] variances, double[] weights, int lo, int hi)
    {
        if (lo >= hi) return;

        int mid = (lo + hi) / 2;

        // Inverse-variance веса для двух половин
        double varLeft  = ClusterVariance(order, variances, lo, mid);
        double varRight = ClusterVariance(order, variances, mid + 1, hi);

        double totalInvVar = (varLeft > 0 ? 1.0 / varLeft : 0)
                           + (varRight > 0 ? 1.0 / varRight : 0);

        double alphaLeft  = totalInvVar > 0 && varLeft > 0
            ? (1.0 / varLeft) / totalInvVar : 0.5;
        double alphaRight = 1.0 - alphaLeft;

        for (int k = lo; k <= mid; k++)   weights[order[k]] *= alphaLeft;
        for (int k = mid + 1; k <= hi; k++) weights[order[k]] *= alphaRight;

        RecursiveBisection(order, variances, weights, lo, mid);
        RecursiveBisection(order, variances, weights, mid + 1, hi);
    }

    private static double ClusterVariance(int[] order, double[] variances, int lo, int hi)
    {
        // Упрощённо: среднее дисперсий (inverse-variance взвешивание внутри кластера)
        double sumInvVar = 0;
        for (int k = lo; k <= hi; k++)
            if (variances[order[k]] > 0)
                sumInvVar += 1.0 / variances[order[k]];

        return sumInvVar > 0 ? 1.0 / sumInvVar : 1.0;
    }
}
