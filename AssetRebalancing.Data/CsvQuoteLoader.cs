using System.Globalization;
using AssetRebalancing.Core;
using CsvHelper;
using CsvHelper.Configuration;

namespace AssetRebalancing.Data;

/// <summary>
/// Реализация <see cref="ICsvQuoteLoader"/>: загружает котировки из CSV-файлов
/// формата «Date,Close», расположенных в папке <c>Data/SampleQuotes/</c>.
/// </summary>
public sealed class CsvQuoteLoader : ICsvQuoteLoader
{
    private const string QuoteSubFolder = "Data/SampleQuotes";

    /// <inheritdoc/>
    public IReadOnlyList<PriceBar> Load(string ticker, string? basePath = null)
    {
        if (string.IsNullOrWhiteSpace(ticker))
            throw new ArgumentException("Тикер не может быть пустым.", nameof(ticker));

        var folder = ResolveFolder(basePath);
        var filePath = Path.Combine(folder, $"{ticker}.csv");

        if (!File.Exists(filePath))
            throw new FileNotFoundException(
                $"Файл котировок для тикера '{ticker}' не найден: {filePath}", filePath);

        return ReadCsv(filePath);
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, IReadOnlyList<PriceBar>> LoadAll(string? basePath = null)
    {
        var folder = ResolveFolder(basePath);

        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException(
                $"Папка с котировками не найдена: {folder}");

        var result = new Dictionary<string, IReadOnlyList<PriceBar>>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.csv"))
        {
            var ticker = Path.GetFileNameWithoutExtension(file);
            result[ticker] = ReadCsv(file);
        }
        return result;
    }

    // ── private helpers ────────────────────────────────────────────────────

    private static string ResolveFolder(string? basePath)
    {
        var root = string.IsNullOrWhiteSpace(basePath)
            ? Directory.GetCurrentDirectory()
            : basePath;
        return Path.Combine(root, QuoteSubFolder);
    }

    private static IReadOnlyList<PriceBar> ReadCsv(string filePath)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            MissingFieldFound = null
        };

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, config);

        csv.Context.RegisterClassMap<PriceBarDtoMap>();
        return csv.GetRecords<PriceBarDto>()
                  .OrderBy(b => b.Date)
                  .Select(b => new PriceBar(DateOnly.Parse(b.Date, CultureInfo.InvariantCulture), b.Close))
                  .ToList()
                  .AsReadOnly();
    }

    // ── Internal DTO + CsvHelper mapping ─────────────────────────────────

    private sealed class PriceBarDto
    {
        public string Date { get; set; } = "";
        public decimal Close { get; set; }
    }

    private sealed class PriceBarDtoMap : ClassMap<PriceBarDto>
    {
        public PriceBarDtoMap()
        {
            Map(m => m.Date).Name("Date");
            Map(m => m.Close).Name("Close");
        }
    }
}
