using AssetRebalancing.Core;

namespace AssetRebalancing.Data;

/// <summary>Загружает исторические котировки из CSV-файлов формата "Date,Close".</summary>
public interface ICsvQuoteLoader
{
    /// <summary>
    /// Загружает котировки для указанного тикера из файла
    /// <c>Data/SampleQuotes/{ticker}.csv</c> (относительно <paramref name="basePath"/>).
    /// </summary>
    /// <param name="ticker">Тикер актива, например "SBERP".</param>
    /// <param name="basePath">Корневая директория проекта. По умолчанию — текущая директория.</param>
    /// <returns>Список котировок, отсортированных по дате по возрастанию.</returns>
    IReadOnlyList<PriceBar> Load(string ticker, string? basePath = null);

    /// <summary>
    /// Загружает котировки для всех CSV-файлов, найденных в папке
    /// <c>Data/SampleQuotes/</c>.
    /// </summary>
    /// <param name="basePath">Корневая директория проекта. По умолчанию — текущая директория.</param>
    IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> LoadAll(string? basePath = null);
}
