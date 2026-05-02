using System.Text;
using AssetRebalancing.Data;
using FluentAssertions;

namespace AssetRebalancing.Tests;

/// <summary>
/// Unit-тесты для <see cref="CsvQuoteLoader"/>.
/// Используют временные CSV-файлы в системной папке /tmp, не зависят от
/// реальных файлов в Data/SampleQuotes/.
/// </summary>
public sealed class CsvQuoteLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _quotesDir;
    private readonly CsvQuoteLoader _loader;

    public CsvQuoteLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hrp_tests_{Guid.NewGuid():N}");
        _quotesDir = Path.Combine(_tempDir, "Data", "SampleQuotes");
        Directory.CreateDirectory(_quotesDir);
        _loader = new CsvQuoteLoader();
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // ── helpers ────────────────────────────────────────────────────────────

    private void WriteCsv(string ticker, string content)
        => File.WriteAllText(Path.Combine(_quotesDir, $"{ticker}.csv"), content, Encoding.UTF8);

    // ── Load: happy path ──────────────────────────────────────────────────

    [Fact]
    public void Load_ValidCsv_ReturnsSortedPriceBars()
    {
        WriteCsv("TEST", """
            Date,Close
            2024-03-01,100.50
            2024-01-15,95.20
            2024-02-10,98.75
            """);

        var bars = _loader.Load("TEST", _tempDir);

        bars.Should().HaveCount(3);
        bars[0].Date.Should().Be(new DateOnly(2024, 1, 15));
        bars[1].Date.Should().Be(new DateOnly(2024, 2, 10));
        bars[2].Date.Should().Be(new DateOnly(2024, 3, 1));
    }

    [Fact]
    public void Load_ValidCsv_CorrectClosePrices()
    {
        WriteCsv("PRICE", """
            Date,Close
            2024-01-10,320.50
            2024-01-17,315.20
            """);

        var bars = _loader.Load("PRICE", _tempDir);

        bars[0].Close.Should().Be(320.50m);
        bars[1].Close.Should().Be(315.20m);
    }

    [Fact]
    public void Load_CsvWithExtraWhitespace_ParsesCorrectly()
    {
        WriteCsv("SPACE", """
            Date , Close
            2024-06-01 , 500.00
            2024-06-02 , 510.25
            """);

        var bars = _loader.Load("SPACE", _tempDir);

        bars.Should().HaveCount(2);
        bars[0].Close.Should().Be(500.00m);
    }

    [Fact]
    public void Load_SingleRow_ReturnsSingleBar()
    {
        WriteCsv("ONE", """
            Date,Close
            2024-01-01,1000.00
            """);

        var bars = _loader.Load("ONE", _tempDir);

        bars.Should().HaveCount(1);
        bars[0].Date.Should().Be(new DateOnly(2024, 1, 1));
        bars[0].Close.Should().Be(1000.00m);
    }

    // ── Load: error cases ─────────────────────────────────────────────────

    [Fact]
    public void Load_TickerNotFound_ThrowsFileNotFoundException()
    {
        var act = () => _loader.Load("MISSING", _tempDir);

        act.Should().Throw<FileNotFoundException>()
           .WithMessage("*MISSING*");
    }

    [Fact]
    public void Load_EmptyTicker_ThrowsArgumentException()
    {
        var act = () => _loader.Load("", _tempDir);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Load_WhitespaceTicker_ThrowsArgumentException()
    {
        var act = () => _loader.Load("   ", _tempDir);

        act.Should().Throw<ArgumentException>();
    }

    // ── LoadAll ────────────────────────────────────────────────────────────

    [Fact]
    public void LoadAll_MultipleCsvFiles_ReturnsAllTickers()
    {
        WriteCsv("AAAA", "Date,Close\n2024-01-01,100.00\n2024-01-02,101.00\n");
        WriteCsv("BBBB", "Date,Close\n2024-01-01,200.00\n2024-01-02,202.00\n");
        WriteCsv("CCCC", "Date,Close\n2024-01-01,300.00\n2024-01-02,303.00\n");

        var all = _loader.LoadAll(_tempDir);

        all.Should().HaveCount(3);
        all.Should().ContainKey("AAAA");
        all.Should().ContainKey("BBBB");
        all.Should().ContainKey("CCCC");
    }

    [Fact]
    public void LoadAll_EmptyFolder_ReturnsEmptyDictionary()
    {
        var all = _loader.LoadAll(_tempDir);

        all.Should().BeEmpty();
    }

    [Fact]
    public void LoadAll_MissingFolder_ThrowsDirectoryNotFoundException()
    {
        var nonExistent = Path.Combine(_tempDir, "nonexistent_base");
        var act = () => _loader.LoadAll(nonExistent);

        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void LoadAll_BarsAreSortedByDate()
    {
        WriteCsv("SORT", """
            Date,Close
            2024-03-01,300.00
            2024-01-01,100.00
            2024-02-01,200.00
            """);

        var bars = _loader.LoadAll(_tempDir)["SORT"];

        bars[0].Date.Should().BeBefore(bars[1].Date);
        bars[1].Date.Should().BeBefore(bars[2].Date);
    }

    // ── LoadAll: ticker name preserved (incl. unicode & dashes) ──────────

    [Fact]
    public void LoadAll_NonAsciiTickerName_ReturnsCorrectKey()
    {
        WriteCsv("ПАРУС-ЛОГ", "Date,Close\n2024-01-01,1025.00\n");

        var all = _loader.LoadAll(_tempDir);

        all.Should().ContainKey("ПАРУС-ЛОГ");
        all["ПАРУС-ЛОГ"].Should().HaveCount(1);
    }

    [Fact]
    public void LoadAll_DashInTickerName_ReturnsCorrectKey()
    {
        WriteCsv("BTC-USD", "Date,Close\n2024-01-01,43250.00\n");

        var all = _loader.LoadAll(_tempDir);

        all.Should().ContainKey("BTC-USD");
    }

    // ── Real sample files smoke test ──────────────────────────────────────

    [Fact]
    public void Load_RealSampleData_AllTickersLoadSuccessfully()
    {
        // Путь к реальным файлам в репозитории (относительно текущей директории сборки)
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is null)
        {
            // В изолированных CI без исходников — пропустить
            return;
        }

        var expectedTickers = new[]
        {
            "SBERP", "ZAYM", "SIBN", "BELU", "TATNP",
            "PLZL", "DOMRF", "PHOR", "MOEX", "MDMG",
            "LKOH", "ПАРУС-ЛОГ", "XXXXXX",
            "BTC-USD", "TAO-USD", "ETH-USD", "USDT-USD"
        };

        foreach (var ticker in expectedTickers)
        {
            var bars = _loader.Load(ticker, repoRoot);
            bars.Should().NotBeEmpty($"файл {ticker}.csv должен содержать котировки");
            bars.Should().HaveCountGreaterOrEqualTo(10, $"{ticker}.csv должен иметь ≥10 строк");
        }
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AssetRebalancing.sln"))
             || File.Exists(Path.Combine(dir.FullName, "AssetRebalancing.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
