using AssetRebalancing.Analytics;
using AssetRebalancing.Core;
using AssetRebalancing.Data;
using AssetRebalancing.Strategy;
using FluentAssertions;
using Xunit.Abstractions;

namespace AssetRebalancing.Tests;

/// <summary>
/// Интеграционные тесты на реальных CSV-котировках.
///
/// Цель — не «угадать» одно магическое число групп, а выбрать разумное k
/// по комбинации факторов:
///   1. отрицательная средняя межгрупповая корреляция,
///   2. структурная разделимость кластеров,
///   3. устойчивость во времени на walk-forward окнах,
///   4. влияние на будущую ребалансировку адаптивного «вечного портфеля».
///
/// Важно: helper-логика живёт в тестах, production-код не модифицируется.
/// </summary>
public sealed class RealQuoteClusteringTests
{
    /// <summary>Загрузчик исторических котировок из CSV-файлов.</summary>
    private readonly CsvQuoteLoader _loader = new();

    /// <summary>Построитель матриц корреляции по временным рядам цен.</summary>
    private readonly CorrelationAnalyzer _analyzer = new();

    /// <summary>Канал диагностического вывода в лог xUnit.</summary>
    private readonly ITestOutputHelper _output;

    public RealQuoteClusteringTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void RealQuotes_CorrelationMatrix_IsSymmetric_AndFinite()
    {
        var context = BuildContextOrSkip();
        if (context is null)
            return;

        context.Tickers.Should().HaveCountGreaterOrEqualTo(8);
        context.CommonBarCount.Should().BeGreaterOrEqualTo(12);

        for (int i = 0; i < context.Tickers.Length; i++)
        {
            context.CorrelationMatrix[i, i].Should().BeApproximately(1.0, 1e-12);

            for (int j = i + 1; j < context.Tickers.Length; j++)
            {
                var left = context.CorrelationMatrix[i, j];
                var right = context.CorrelationMatrix[j, i];

                left.Should().NotBe(double.NaN);
                right.Should().NotBe(double.NaN);
                left.Should().BeInRange(-1.0, 1.0);
                right.Should().BeInRange(-1.0, 1.0);
                left.Should().BeApproximately(right, 1e-12);
            }
        }
    }

    [Fact]
    public void RealQuotes_CandidateGroupings_AreValid_AndReachNegativeInterGroupCorrelation()
    {
        var context = BuildContextOrSkip();
        if (context is null)
            return;

        var evaluations = EvaluateCandidates(context).ToArray();
        evaluations.Should().NotBeEmpty();

        foreach (var evaluation in evaluations)
        {
            evaluation.Groups.Should().HaveCount(evaluation.GroupCount);
            evaluation.Groups.SelectMany(g => g).Should().OnlyHaveUniqueItems();
            evaluation.Groups.SelectMany(g => g).Should().BeEquivalentTo(context.Tickers);
            evaluation.Groups.Should().OnlyContain(g => g.Count > 0);
        }

        evaluations.Min(e => e.MeanInterGroupCorrelation)
            .Should().BeLessThan(0.0,
                "на реальных данных должен существовать разрез дендрограммы с отрицательной средней межгрупповой корреляцией");

        var bestNegativeCorr = evaluations.MinBy(e => e.MeanInterGroupCorrelation)!;
        _output.WriteLine($"Лучший по отрицательной межгрупповой корреляции: k={bestNegativeCorr.GroupCount}, avgInterCorr={bestNegativeCorr.MeanInterGroupCorrelation:F4}");
        WriteEvaluations(evaluations);
    }

    [Fact]
    public void RealQuotes_SelectedGroupCount_BalancesStructureStability_AndFutureRebalancing()
    {
        var context = BuildContextOrSkip();
        if (context is null)
            return;

        var evaluations = EvaluateCandidates(context)
            .OrderByDescending(e => e.CompositeScore)
            .ThenBy(e => e.MeanInterGroupCorrelation)
            .ThenBy(e => e.GroupCount)
            .ToArray();

        evaluations.Should().HaveCountGreaterOrEqualTo(3);

        var selected = evaluations[0];
        var bestNegativeCorr = evaluations.OrderBy(e => e.MeanInterGroupCorrelation).First();
        var bestStability = evaluations.OrderByDescending(e => e.TemporalStabilityScore).First();
        var bestRebalance = evaluations.OrderBy(e => e.FutureRebalanceImpact).First();

        WriteEvaluations(evaluations);
        WriteGroups("Выбранное разбиение", selected.Groups);

        selected.CompositeScore.Should().BeApproximately(evaluations.Max(e => e.CompositeScore), 1e-12);
        selected.NormalizedNegativeCorrelationScore.Should().BeGreaterOrEqualTo(Median(evaluations.Select(e => e.NormalizedNegativeCorrelationScore)));
        selected.NormalizedStructureScore.Should().BeGreaterOrEqualTo(Median(evaluations.Select(e => e.NormalizedStructureScore)));
        selected.NormalizedStabilityScore.Should().BeGreaterThan(evaluations.Min(e => e.NormalizedStabilityScore));
        selected.NormalizedRebalanceScore.Should().BeGreaterThan(evaluations.Min(e => e.NormalizedRebalanceScore));

        selected.MeanInterGroupCorrelation.Should().BeLessOrEqualTo(bestNegativeCorr.MeanInterGroupCorrelation + 0.10,
            "лучшее k по комбинированному score не должно слишком далеко уходить от лучшего k по отрицательной межгрупповой корреляции");

        selected.TemporalStabilityScore.Should().BeGreaterOrEqualTo(bestStability.TemporalStabilityScore - 0.15,
            "выбранное k должно оставаться близким к лучшему по устойчивости во времени");

        selected.FutureRebalanceImpact.Should().BeLessOrEqualTo(bestRebalance.FutureRebalanceImpact + 0.25,
            "выбранное k не должно заметно ухудшать будущую ребалансировку по сравнению с лучшим кандидатом");
    }

    private AnalysisContext? BuildContextOrSkip()
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is null)
        {
            _output.WriteLine("Репозиторий с исходными CSV не найден — тест пропущен.");
            return null;
        }

        var rawQuotes = _loader.LoadAll(repoRoot);
        var alignedQuotes = AlignByCommonDates(rawQuotes);
        if (alignedQuotes.Count < 8)
        {
            _output.WriteLine($"Недостаточно рядов после выравнивания по общим датам: {alignedQuotes.Count}");
            return null;
        }

        var commonBarCount = alignedQuotes.Values.Min(x => x.Count);
        if (commonBarCount < 12)
        {
            _output.WriteLine($"Недостаточно общих котировок для анализа: {commonBarCount}");
            return null;
        }

        var (tickers, corr) = _analyzer.BuildCorrelationMatrix(alignedQuotes);
        var dist = ToDistanceMatrix(corr, tickers.Length);
        var root = BuildDendrogram(tickers, dist);
        var windows = BuildWalkForwardWindows(alignedQuotes).ToArray();

        if (windows.Length < 3)
        {
            _output.WriteLine($"Недостаточно walk-forward окон: {windows.Length}");
            return null;
        }

        _output.WriteLine($"Загружено тикеров: {tickers.Length}, общих дат: {commonBarCount}, walk-forward окон: {windows.Length}");

        return new AnalysisContext(tickers, corr, dist, root, windows, commonBarCount);
    }

    private IReadOnlyList<CandidateEvaluation> EvaluateCandidates(AnalysisContext context)
    {
        int maxGroups = Math.Min(6, context.Tickers.Length - 1);
        var raw = new List<CandidateRawMetrics>();

        for (int groupCount = 2; groupCount <= maxGroups; groupCount++)
        {
            var groups = CutTree(context.RootCluster, groupCount, context.Tickers);
            var meanInterGroupCorrelation = ComputeMeanInterGroupCorrelation(context.CorrelationMatrix, context.Tickers, groups);
            var structureScore = ComputeStructureScore(context.DistanceMatrix, context.Tickers, groups);
            var temporalStability = TemporalStabilityScore(context.Windows, groupCount);
            var rebalanceImpact = FutureRebalanceImpact(context.Windows, groupCount);

            raw.Add(new CandidateRawMetrics(
                groupCount,
                groups,
                meanInterGroupCorrelation,
                structureScore,
                temporalStability,
                rebalanceImpact));
        }

        var negativeScores = raw.Select(x => -x.MeanInterGroupCorrelation).ToArray();
        var structureScores = raw.Select(x => x.StructureScore).ToArray();
        var stabilityScores = raw.Select(x => x.TemporalStabilityScore).ToArray();
        var rebalanceScores = raw.Select(x => x.FutureRebalanceImpact).ToArray();

        return raw.Select(x =>
        {
            var normNegative = NormalizeHigherIsBetter(-x.MeanInterGroupCorrelation, negativeScores);
            var normStructure = NormalizeHigherIsBetter(x.StructureScore, structureScores);
            var normStability = NormalizeHigherIsBetter(x.TemporalStabilityScore, stabilityScores);
            var normRebalance = NormalizeLowerIsBetter(x.FutureRebalanceImpact, rebalanceScores);

            var composite = 0.35 * normNegative
                          + 0.25 * normStructure
                          + 0.20 * normStability
                          + 0.20 * normRebalance;

            return new CandidateEvaluation(
                x.GroupCount,
                x.Groups,
                x.MeanInterGroupCorrelation,
                x.StructureScore,
                x.TemporalStabilityScore,
                x.FutureRebalanceImpact,
                normNegative,
                normStructure,
                normStability,
                normRebalance,
                composite);
        }).OrderBy(x => x.GroupCount).ToArray();
    }

    private double TemporalStabilityScore(IReadOnlyList<WalkForwardWindow> windows, int groupCount)
    {
        var partitions = windows
            .Select(window => BuildPartition(window.TrainQuotes, groupCount))
            .ToArray();

        if (partitions.Length < 2)
            return 1.0;

        var scores = new List<double>();
        for (int i = 1; i < partitions.Length; i++)
            scores.Add(PairwisePartitionAgreement(partitions[i - 1], partitions[i]));

        return scores.Count == 0 ? 1.0 : scores.Average();
    }

    private double FutureRebalanceImpact(IReadOnlyList<WalkForwardWindow> windows, int groupCount)
    {
        var rebalancer = new ThresholdRebalancer();
        var impacts = new List<double>();

        foreach (var window in windows)
        {
            var groups = BuildPartition(window.TrainQuotes, groupCount);
            impacts.Add(ComputeWindowRebalanceImpact(groups, window.FutureQuotes, rebalancer));
        }

        return impacts.Count == 0 ? 0.0 : impacts.Average();
    }

    private IReadOnlyList<IReadOnlyList<string>> BuildPartition(
        IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> quotes,
        int groupCount)
    {
        var (tickers, corr) = _analyzer.BuildCorrelationMatrix(quotes);
        var dist = ToDistanceMatrix(corr, tickers.Length);
        var root = BuildDendrogram(tickers, dist);
        return CutTree(root, groupCount, tickers);
    }

    private static double ComputeWindowRebalanceImpact(
        IReadOnlyList<IReadOnlyList<string>> groups,
        IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> futureQuotes,
        ThresholdRebalancer rebalancer)
    {
        int k = groups.Count;
        decimal targetWeight = 1m / k;

        var assets = new List<Asset>();
        var targetWeights = new Dictionary<Asset, decimal>();
        var currentWeights = new Dictionary<Asset, decimal>();
        var growths = groups.Select(group => ComputeGroupGrowth(group, futureQuotes)).ToArray();
        var weightedTotal = growths.Sum(g => targetWeight * (decimal)g);

        for (int i = 0; i < groups.Count; i++)
        {
            var label = string.Join(", ", groups[i].Take(3));
            if (groups[i].Count > 3)
                label += ", …";

            var asset = new Asset($"GROUP-{i + 1}", label, AssetClass.Equity);
            assets.Add(asset);
            targetWeights[asset] = targetWeight;

            var currentWeight = weightedTotal > 0m
                ? (targetWeight * (decimal)growths[i]) / weightedTotal
                : targetWeight;

            currentWeights[asset] = currentWeight;
        }

        var portfolio = new Portfolio
        {
            TargetWeights = targetWeights,
            CurrentWeights = currentWeights
        };

        var orders = rebalancer.Plan(portfolio);
        var normalizedOrderCount = k == 0 ? 0.0 : (double)orders.Count / k;
        var totalDrift = assets.Sum(asset => Math.Abs((double)(currentWeights[asset] - targetWeights[asset])));

        return normalizedOrderCount + totalDrift;
    }

    private static double ComputeGroupGrowth(
        IReadOnlyList<string> group,
        IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> futureQuotes)
    {
        var returns = new List<double>();

        foreach (var ticker in group)
        {
            var bars = futureQuotes[ticker];
            if (bars.Count < 2)
                continue;

            var start = (double)bars[0].Close;
            var end = (double)bars[^1].Close;
            if (start > 0)
                returns.Add(end / start);
        }

        return returns.Count == 0 ? 1.0 : returns.Average();
    }

    private static double PairwisePartitionAgreement(
        IReadOnlyList<IReadOnlyList<string>> left,
        IReadOnlyList<IReadOnlyList<string>> right)
    {
        var tickers = left.SelectMany(x => x).OrderBy(x => x).ToArray();
        var leftMap = ToGroupMap(left);
        var rightMap = ToGroupMap(right);

        int total = 0;
        int agreed = 0;

        for (int i = 0; i < tickers.Length; i++)
        {
            for (int j = i + 1; j < tickers.Length; j++)
            {
                total++;
                bool leftTogether = leftMap[tickers[i]] == leftMap[tickers[j]];
                bool rightTogether = rightMap[tickers[i]] == rightMap[tickers[j]];
                if (leftTogether == rightTogether)
                    agreed++;
            }
        }

        return total == 0 ? 1.0 : (double)agreed / total;
    }

    private static Dictionary<string, int> ToGroupMap(IReadOnlyList<IReadOnlyList<string>> groups)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < groups.Count; i++)
            foreach (var ticker in groups[i])
                map[ticker] = i;
        return map;
    }

    private static double ComputeMeanInterGroupCorrelation(
        double[,] corr,
        IReadOnlyList<string> tickers,
        IReadOnlyList<IReadOnlyList<string>> groups)
    {
        var groupMap = ToGroupMap(groups);
        double sum = 0;
        int count = 0;

        for (int i = 0; i < tickers.Count; i++)
        {
            for (int j = i + 1; j < tickers.Count; j++)
            {
                if (groupMap[tickers[i]] == groupMap[tickers[j]])
                    continue;

                sum += corr[i, j];
                count++;
            }
        }

        return count == 0 ? 0.0 : sum / count;
    }

    private static double ComputeStructureScore(
        double[,] dist,
        IReadOnlyList<string> tickers,
        IReadOnlyList<IReadOnlyList<string>> groups)
    {
        var groupMap = ToGroupMap(groups);
        double withinSum = 0;
        int withinCount = 0;
        double betweenSum = 0;
        int betweenCount = 0;

        for (int i = 0; i < tickers.Count; i++)
        {
            for (int j = i + 1; j < tickers.Count; j++)
            {
                if (groupMap[tickers[i]] == groupMap[tickers[j]])
                {
                    withinSum += dist[i, j];
                    withinCount++;
                }
                else
                {
                    betweenSum += dist[i, j];
                    betweenCount++;
                }
            }
        }

        var withinMean = withinCount == 0 ? 0.0 : withinSum / withinCount;
        var betweenMean = betweenCount == 0 ? 0.0 : betweenSum / betweenCount;
        return betweenMean - withinMean;
    }

    private static IReadOnlyList<IReadOnlyList<string>> CutTree(
        ClusterNode root,
        int groupCount,
        IReadOnlyList<string> tickers)
    {
        var frontier = new List<ClusterNode> { root };

        while (frontier.Count < groupCount)
        {
            var toSplit = frontier
                .Where(node => !node.IsLeaf)
                .OrderByDescending(node => node.Distance)
                .ThenByDescending(node => node.LeafIndices.Count)
                .FirstOrDefault();

            if (toSplit is null || toSplit.Left is null || toSplit.Right is null)
                break;

            frontier.Remove(toSplit);
            frontier.Add(toSplit.Left);
            frontier.Add(toSplit.Right);
        }

        return frontier
            .Select(node => (IReadOnlyList<string>)node.LeafIndices
                .Select(index => tickers[index])
                .OrderBy(ticker => ticker)
                .ToArray())
            .OrderBy(group => group[0])
            .ToArray();
    }

    private static ClusterNode BuildDendrogram(IReadOnlyList<string> tickers, double[,] dist)
    {
        var clusters = Enumerable.Range(0, tickers.Count)
            .Select(index => new ClusterNode(index, 0.0, new[] { index }, null, null))
            .ToList();

        int nextId = tickers.Count;
        while (clusters.Count > 1)
        {
            double minDist = double.MaxValue;
            int leftIndex = 0;
            int rightIndex = 1;

            for (int i = 0; i < clusters.Count; i++)
            {
                for (int j = i + 1; j < clusters.Count; j++)
                {
                    var candidateDist = SingleLinkageDistance(clusters[i], clusters[j], dist);
                    if (candidateDist < minDist - 1e-12)
                    {
                        minDist = candidateDist;
                        leftIndex = i;
                        rightIndex = j;
                    }
                }
            }

            var left = clusters[leftIndex];
            var right = clusters[rightIndex];
            var merged = new ClusterNode(
                nextId++,
                minDist,
                left.LeafIndices.Concat(right.LeafIndices).ToArray(),
                left,
                right);

            clusters[leftIndex] = merged;
            clusters.RemoveAt(rightIndex);
        }

        return clusters[0];
    }

    private static double SingleLinkageDistance(ClusterNode left, ClusterNode right, double[,] dist)
    {
        double min = double.MaxValue;

        foreach (var i in left.LeafIndices)
        {
            foreach (var j in right.LeafIndices)
                min = Math.Min(min, dist[i, j]);
        }

        return min;
    }

    private static double[,] ToDistanceMatrix(double[,] corr, int n)
    {
        var dist = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
                dist[i, j] = Math.Sqrt(Math.Max(0.0, 0.5 * (1.0 - corr[i, j])));
        }

        return dist;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> AlignByCommonDates(
        IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> rawQuotes)
    {
        var ordered = rawQuotes
            .Where(x => x.Value.Count >= 12)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToArray();

        var commonDates = ordered
            .Select(x => x.Value.Select(bar => bar.Date).ToHashSet())
            .Aggregate((left, right) =>
            {
                left.IntersectWith(right);
                return left;
            })
            .OrderBy(date => date)
            .ToHashSet();

        return ordered.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<PriceBar>)x.Value
                .Where(bar => commonDates.Contains(bar.Date) && bar.Close > 0m)
                .OrderBy(bar => bar.Date)
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyList<WalkForwardWindow> BuildWalkForwardWindows(
        IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> quotes)
    {
        int totalBars = quotes.Values.Min(x => x.Count);
        int trainBars = Math.Max(10, totalBars / 2 + 1);
        int futureBars = Math.Max(4, totalBars / 5);
        int step = Math.Max(2, futureBars / 2);

        var windows = new List<WalkForwardWindow>();
        for (int start = 0; start + trainBars + futureBars <= totalBars; start += step)
        {
            var trainQuotes = SliceQuotes(quotes, start, trainBars);
            var futureQuotes = SliceQuotes(quotes, start + trainBars - 1, futureBars);

            if (trainQuotes.Count > 0 && futureQuotes.Count > 0)
                windows.Add(new WalkForwardWindow(trainQuotes, futureQuotes));
        }

        return windows;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> SliceQuotes(
        IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> quotes,
        int skip,
        int take)
    {
        return quotes.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<PriceBar>)x.Value.Skip(skip).Take(take).ToArray(),
            StringComparer.Ordinal);
    }

    private static double NormalizeHigherIsBetter(double value, IReadOnlyList<double> values)
    {
        double min = values.Min();
        double max = values.Max();
        if (Math.Abs(max - min) < 1e-12)
            return 0.5;

        return (value - min) / (max - min);
    }

    private static double NormalizeLowerIsBetter(double value, IReadOnlyList<double> values)
    {
        double min = values.Min();
        double max = values.Max();
        if (Math.Abs(max - min) < 1e-12)
            return 0.5;

        return (max - value) / (max - min);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(x => x).ToArray();
        if (ordered.Length == 0)
            return 0.0;

        int mid = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[mid]
            : (ordered[mid - 1] + ordered[mid]) / 2.0;
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AssetRebalancing.sln"))
             || File.Exists(Path.Combine(dir.FullName, "AssetRebalancing.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }

    private void WriteEvaluations(IEnumerable<CandidateEvaluation> evaluations)
    {
        foreach (var evaluation in evaluations.OrderBy(x => x.GroupCount))
        {
            _output.WriteLine(
                $"k={evaluation.GroupCount}: " +
                $"avgInterCorr={evaluation.MeanInterGroupCorrelation:F4}; " +
                $"structure={evaluation.StructureScore:F4}; " +
                $"stability={evaluation.TemporalStabilityScore:F4}; " +
                $"rebalance={evaluation.FutureRebalanceImpact:F4}; " +
                $"composite={evaluation.CompositeScore:F4}");
        }
    }

    private void WriteGroups(string title, IReadOnlyList<IReadOnlyList<string>> groups)
    {
        _output.WriteLine(title);
        for (int i = 0; i < groups.Count; i++)
            _output.WriteLine($"  G{i + 1}: {string.Join(", ", groups[i])}");
    }

    /// <summary>
    /// Контекст одного полного анализа по реальным котировкам:
    /// тикеры, корреляции, дендрограмма и walk-forward окна.
    /// </summary>
    /// <param name="Tickers">Упорядоченный список тикеров, соответствующий индексам матриц.</param>
    /// <param name="CorrelationMatrix">Матрица корреляций активов.</param>
    /// <param name="DistanceMatrix">Матрица расстояний, полученная из корреляций.</param>
    /// <param name="RootCluster">Корневой узел восстановленной дендрограммы.</param>
    /// <param name="Windows">Набор walk-forward окон для проверки устойчивости и ребалансировки.</param>
    /// <param name="CommonBarCount">Количество общих точек наблюдения после выравнивания рядов по датам.</param>
    private sealed record AnalysisContext(
        string[] Tickers,
        double[,] CorrelationMatrix,
        double[,] DistanceMatrix,
        ClusterNode RootCluster,
        IReadOnlyList<WalkForwardWindow> Windows,
        int CommonBarCount);

    /// <summary>
    /// Одно walk-forward окно: обучающая часть для построения групп и будущая часть для проверки их поведения.
    /// </summary>
    /// <param name="TrainQuotes">Котировки на обучающем интервале.</param>
    /// <param name="FutureQuotes">Котировки на последующем out-of-sample интервале.</param>
    private sealed record WalkForwardWindow(
        IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> TrainQuotes,
        IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> FutureQuotes);

    /// <summary>
    /// Узел дендрограммы single-linkage clustering.
    /// </summary>
    /// <param name="Id">Уникальный идентификатор узла внутри тестовой дендрограммы.</param>
    /// <param name="Distance">Расстояние, на котором был сформирован кластер.</param>
    /// <param name="LeafIndices">Индексы листьев, входящих в данный кластер.</param>
    /// <param name="Left">Левый дочерний кластер.</param>
    /// <param name="Right">Правый дочерний кластер.</param>
    private sealed record ClusterNode(
        int Id,
        double Distance,
        IReadOnlyList<int> LeafIndices,
        ClusterNode? Left,
        ClusterNode? Right)
    {
        /// <summary>Показывает, является ли узел листом, а не объединением двух подкластеров.</summary>
        public bool IsLeaf => Left is null || Right is null;
    }

    /// <summary>
    /// Ненормализованные метрики кандидата на число групп.
    /// </summary>
    /// <param name="GroupCount">Количество групп в разрезе дендрограммы.</param>
    /// <param name="Groups">Состав групп для данного кандидата.</param>
    /// <param name="MeanInterGroupCorrelation">Средняя корреляция между активами из разных групп.</param>
    /// <param name="StructureScore">Оценка отделимости групп по матрице расстояний.</param>
    /// <param name="TemporalStabilityScore">Оценка устойчивости разбиения на соседних walk-forward окнах.</param>
    /// <param name="FutureRebalanceImpact">Оценка будущего дрейфа и объёма ребалансировки для данного кандидата.</param>
    private sealed record CandidateRawMetrics(
        int GroupCount,
        IReadOnlyList<IReadOnlyList<string>> Groups,
        double MeanInterGroupCorrelation,
        double StructureScore,
        double TemporalStabilityScore,
        double FutureRebalanceImpact);

    /// <summary>
    /// Полная оценка кандидата на число групп, включая нормализованные компоненты и итоговый score.
    /// </summary>
    /// <param name="GroupCount">Количество групп в разрезе дендрограммы.</param>
    /// <param name="Groups">Состав групп для данного кандидата.</param>
    /// <param name="MeanInterGroupCorrelation">Средняя межгрупповая корреляция в исходной шкале.</param>
    /// <param name="StructureScore">Структурная отделимость кластеров в исходной шкале.</param>
    /// <param name="TemporalStabilityScore">Устойчивость групп во времени в исходной шкале.</param>
    /// <param name="FutureRebalanceImpact">Влияние кандидата на будущую ребалансировку в исходной шкале.</param>
    /// <param name="NormalizedNegativeCorrelationScore">Нормализованная полезность отрицательной межгрупповой корреляции.</param>
    /// <param name="NormalizedStructureScore">Нормализованная оценка структурной отделимости.</param>
    /// <param name="NormalizedStabilityScore">Нормализованная оценка временной устойчивости.</param>
    /// <param name="NormalizedRebalanceScore">Нормализованная оценка влияния на будущую ребалансировку.</param>
    /// <param name="CompositeScore">
    /// Итоговый score для выбора числа групп k среди кандидатов.
    /// Вычисляется как взвешенная сумма четырёх нормализованных компонент:
    /// 0.35 × полезность отрицательной межгрупповой корреляции,
    /// 0.25 × структурная разделимость кластеров,
    /// 0.20 × устойчивость разбиения во времени,
    /// 0.20 × благоприятность будущей ребалансировки.
    /// Score не является самостоятельной финансовой метрикой;
    /// он служит агрегированным критерием ранжирования кандидатов по качеству компромисса.
    /// </param>
    private sealed record CandidateEvaluation(
        int GroupCount,
        IReadOnlyList<IReadOnlyList<string>> Groups,
        double MeanInterGroupCorrelation,
        double StructureScore,
        double TemporalStabilityScore,
        double FutureRebalanceImpact,
        double NormalizedNegativeCorrelationScore,
        double NormalizedStructureScore,
        double NormalizedStabilityScore,
        double NormalizedRebalanceScore,
        double CompositeScore);
}



