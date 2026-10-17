using System.Text.RegularExpressions;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// AC2/AC14 (ADR 0098): pins the shape of the prod-only <c>doc-chunks</c> SQL behind <c>read_document</c>
/// and re-ingest-replaces. <see cref="CosmosDocumentIndex"/> has no offline stand-in, but its query text is
/// pure string construction, so the two rules that would otherwise fail only in prod are checked here: a
/// single-property ORDER BY (the <c>doc-chunks</c> container declares no composite index — see
/// <see cref="CompositeIndexMatchesTheSearchQueryTests"/> for the bug a multi-property one causes), and
/// <c>fileName</c> as a parameter, never interpolated.
/// </summary>
public sealed class DocumentChunkQueryShapeTests
{
    [Fact]
    public void ReadRange_OrdersByChunkIndexAlone_Ascending()
    {
        var orderBy = Regex.Match(CosmosDocumentIndex.ReadRangeSql, @"ORDER BY (?<paths>.+)$").Groups["paths"].Value;

        Assert.Equal("c.chunkIndex", orderBy.Trim());
        Assert.DoesNotContain("DESC", CosmosDocumentIndex.ReadRangeSql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CosmosDocumentIndex.ReadRangeSql)]
    [InlineData(CosmosDocumentIndex.MaxChunkIndexSql)]
    [InlineData(CosmosDocumentIndex.DeleteFileScanSql)]
    public void EveryQuery_FiltersByAFileNameParameter(string sql)
        => Assert.Contains("c.fileName = @fileName", sql);

    [Fact]
    public void ReadRange_BoundsAreParameters()
    {
        Assert.Contains("c.chunkIndex >= @start", CosmosDocumentIndex.ReadRangeSql);
        Assert.Contains("c.chunkIndex < @end", CosmosDocumentIndex.ReadRangeSql);
    }

    // The total is MAX + 1, not COUNT: COUNT over-counts legacy duplicate copies.
    [Fact]
    public void Total_IsMaxPlusOne_NotACount()
    {
        Assert.Contains("MAX(c.chunkIndex)", CosmosDocumentIndex.MaxChunkIndexSql);
        Assert.DoesNotContain("COUNT", CosmosDocumentIndex.MaxChunkIndexSql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(12, CosmosDocumentIndex.TotalChunksFromMax([11]));
    }

    // AC14: Cosmos MAX over no rows is an EMPTY result set (or undefined), not 0.
    [Fact]
    public void Total_EmptyOrUndefinedMax_IsZero()
    {
        Assert.Equal(0, CosmosDocumentIndex.TotalChunksFromMax([]));
        Assert.Equal(0, CosmosDocumentIndex.TotalChunksFromMax([null]));
        Assert.Equal(1, CosmosDocumentIndex.TotalChunksFromMax([0]));
    }
}
