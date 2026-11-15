using AiOps.Mcp.Inventory.Search;

namespace AiOps.Mcp.Tests;

public class QdrantPointIdTests
{
    [Fact]
    public void Same_server_different_casing_yields_one_point()
        => Assert.Equal(QdrantVectorIndex.PointId("sql01.contoso.local"), QdrantVectorIndex.PointId("SQL01.CONTOSO.LOCAL"));

    [Fact]
    public void Ascii_i_folds_identically_even_under_turkish_culture()
    {
        // The CLAUDE.md 'I/ı' trap: culture-sensitive ToUpper on tr-TR maps 'i' → 'İ',
        // which would split one server into two points. Invariant casing must not.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal(
                QdrantVectorIndex.PointId("istanbul-srv01"),
                QdrantVectorIndex.PointId("ISTANBUL-SRV01"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void Different_servers_yield_different_points()
        => Assert.NotEqual(QdrantVectorIndex.PointId("srv1"), QdrantVectorIndex.PointId("srv2"));

    [Fact]
    public void Point_id_scheme_is_frozen()
        // Hard-coded expectation: if the hashing scheme ever changes, every server would
        // silently get a duplicate point in Qdrant on the next sweep.
        => Assert.Equal(
            Guid.Parse("b467e6ae-9c4f-1e89-c795-a22c93f4ec0b"),
            QdrantVectorIndex.PointId("srv1.contoso.local"));
}
