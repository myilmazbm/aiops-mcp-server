using AiOps.Mcp.Remoting;

namespace AiOps.Mcp.Tests;

public class CounterCatalogTests
{
    private static readonly IReadOnlyDictionary<int, string> TurkishTable = new Dictionary<int, string>
    {
        [238] = "İşlemci",
        [6] = "% İşlemci Zamanı",
        [4] = "Bellek",
        [1382] = "Kullanılabilir MB",
        [236] = "Mantıksal Disk",
        [408] = "% Boş Alan",
    };

    [Theory]
    [InlineData(@"\Processor(_Total)\% Processor Time", "Processor", "_Total", "% Processor Time")]
    [InlineData(@"\Memory\Available MBytes", "Memory", null, "Available MBytes")]
    [InlineData(@"\Process(w3wp#1)\Working Set", "Process", "w3wp#1", "Working Set")]
    [InlineData(@"\Process(app(x))\Working Set", "Process", "app(x)", "Working Set")]
    [InlineData(@"\LogicalDisk(*)\% Free Space", "LogicalDisk", "*", "% Free Space")]
    public void Parses_valid_paths(string path, string expectedObject, string? expectedInstance, string expectedCounter)
    {
        Assert.True(CounterCatalog.TryParsePath(path, out var obj, out var instance, out var counter));
        Assert.Equal(expectedObject, obj);
        Assert.Equal(expectedInstance, instance);
        Assert.Equal(expectedCounter, counter);
    }

    [Theory]
    [InlineData(@"\\SRV1\Processor(_Total)\% Processor Time")] // machine prefix not accepted
    [InlineData(@"Processor\% Processor Time")]
    [InlineData(@"\JustOneSegment")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_invalid_paths(string path)
        => Assert.False(CounterCatalog.TryParsePath(path, out _, out _, out _));

    [Fact]
    public void Translates_to_target_locale_by_pdh_index()
    {
        var translated = CounterCatalog.Translate(@"\Processor(_Total)\% Processor Time", TurkishTable);

        Assert.Equal(@"\İşlemci(_Total)\% İşlemci Zamanı", translated);
    }

    [Fact]
    public void Translates_instance_free_path()
    {
        var translated = CounterCatalog.Translate(@"\Memory\Available MBytes", TurkishTable);

        Assert.Equal(@"\Bellek\Kullanılabilir MB", translated);
    }

    [Fact]
    public void Wildcard_instance_survives_translation()
    {
        var translated = CounterCatalog.Translate(@"\LogicalDisk(*)\% Free Space", TurkishTable);

        Assert.Equal(@"\Mantıksal Disk(*)\% Boş Alan", translated);
    }

    [Fact]
    public void Unknown_counter_name_returns_null()
        => Assert.Null(CounterCatalog.Translate(@"\Processor(_Total)\Totally Made Up", TurkishTable));

    [Fact]
    public void Index_missing_from_target_table_returns_null()
    {
        var sparse = new Dictionary<int, string> { [238] = "İşlemci" }; // no entry for counter 6

        Assert.Null(CounterCatalog.Translate(@"\Processor(_Total)\% Processor Time", sparse));
    }

    [Fact]
    public void Catalog_lookup_is_case_insensitive()
    {
        var translated = CounterCatalog.Translate(@"\PROCESSOR(_Total)\% processor time", TurkishTable);

        Assert.Equal(@"\İşlemci(_Total)\% İşlemci Zamanı", translated);
    }

    [Fact]
    public void Parses_perflib_multi_sz_pairs()
    {
        var table = CounterCatalog.ParsePerflibTable(
            ["1", "1847", "2", "System", "4", "Memory", "not-a-number", "Garbage", "4", "Duplicate", "6"]);

        Assert.Equal("System", table[2]);
        Assert.Equal("Memory", table[4]); // first entry wins over the duplicate
        Assert.Equal("1847", table[1]);
        Assert.Equal(3, table.Count); // garbage and the odd trailing element are skipped
    }
}
