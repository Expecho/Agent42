using System.Text.RegularExpressions;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// Ties <see cref="CosmosChatSearchIndex.BuildSearchSql"/>'s <c>ORDER BY</c> to the composite index
/// declared for the <c>chat-messages</c> container in <c>infra/modules/cosmos.bicep</c> (ADR 0061),
/// in the source-text style of <see cref="EasyAuthExclusionTests"/>.
///
/// <para><b>The bug it exists to stop from coming back.</b> Cosmos serves a single-property
/// <c>ORDER BY</c> from the default range index, but a <b>multi</b>-property one is a hard 400 —
/// <i>"The order by query does not have a corresponding composite index that it can be served
/// from"</i> — unless that exact path sequence is declared as a composite index. The container
/// shipped without one, so every <c>GET /api/search</c> with a non-blank query failed in production
/// while the Bicep, the ADR and the whole suite all looked right.</para>
///
/// <para>It hid unusually well, and that is why this is pinned rather than left to a prod smoke.
/// Writes never touch the index — <c>IndexTurnsAsync</c> is an upsert, which runs no query — so the
/// corpus filled up normally and only reads were broken; and <c>search.js</c> maps a non-ok response
/// to zero hits, so the 400 rendered as the ordinary "no matches" row rather than as an error. The
/// adapter is a prod-only path with no offline stand-in (the position <c>CosmosDocumentIndex</c> has
/// held since ADR 0007), but this rule is pure string construction on both sides, so it needs no
/// Cosmos account to check.</para>
/// </summary>
public sealed class CompositeIndexMatchesTheSearchQueryTests
{
    [Fact]
    public void EveryMultiPropertyOrderBy_HasAMatchingCompositeIndex()
    {
        var orderBy = OrderByPaths();

        // Cosmos only requires a composite index once the ORDER BY names two or more properties, so
        // that — not "an ORDER BY exists" — is the condition this encodes. A future single-property
        // ordering is legitimately free of a composite index.
        if (orderBy.Count < 2)
        {
            return;
        }

        var declared = CompositeIndexes();

        Assert.Contains(declared, index => index.SequenceEqual(orderBy));
    }

    /// <summary>
    /// The ordering is <b>ascending</b> in the SQL (no <c>DESC</c>), and a composite index only
    /// serves an ORDER BY whose directions match it. Asserted separately so a direction drift on
    /// either side names itself instead of surfacing as a generic "no matching index".
    /// </summary>
    [Fact]
    public void TheDeclaredCompositeIndex_IsAscendingOnEveryPath()
    {
        var orders = CompositeIndexEntries().Select(entry => entry.Order).ToArray();

        Assert.NotEmpty(orders);
        Assert.All(orders, order => Assert.Equal("ascending", order));
    }

    /// <summary>
    /// The ORDER BY path sequence of the shipped search query, as Cosmos index paths
    /// (<c>c.conversationId</c> becomes <c>/conversationId</c>).
    /// </summary>
    private static IReadOnlyList<string> OrderByPaths()
    {
        var sql = CosmosChatSearchIndex.BuildSearchSql(25);

        var at = sql.IndexOf("ORDER BY ", StringComparison.Ordinal);
        Assert.True(at >= 0, "No ORDER BY found in BuildSearchSql — did the query change shape?");

        var clause = sql[(at + "ORDER BY ".Length)..];

        var paths = clause
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // Tolerate an explicit ASC/DESC: the path is the first token of each segment.
            .Select(segment => segment.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .Select(reference => "/" + reference.Split('.').Last())
            .ToList();

        // A tautology guard: a broken extraction would otherwise make the test above pass vacuously
        // by returning fewer than two paths.
        Assert.NotEmpty(paths);
        return paths;
    }

    /// <summary>Each declared composite index, as its ordered list of paths.</summary>
    private static IReadOnlyList<IReadOnlyList<string>> CompositeIndexes()
    {
        var groups = new List<IReadOnlyList<string>>();
        var current = new List<string>();

        // The entries arrive in document order, grouped by the inner array each one opened.
        foreach (var entry in CompositeIndexEntries())
        {
            if (entry.StartsGroup && current.Count > 0)
            {
                groups.Add(current);
                current = [];
            }

            current.Add(entry.Path);
        }

        if (current.Count > 0)
        {
            groups.Add(current);
        }

        Assert.NotEmpty(groups);
        return groups;
    }

    private static IReadOnlyList<(string Path, string Order, bool StartsGroup)> CompositeIndexEntries()
    {
        var block = CompositeIndexesBlock();
        var entries = new List<(string, string, bool)>();

        // Walk the block once so an inner "[" can mark the entry that follows it as opening a new
        // composite index — compositeIndexes is an array OF arrays, one per ORDER BY combination.
        var startsGroup = false;
        foreach (Match match in Regex.Matches(block, @"\[|\{[^}]*\}"))
        {
            if (match.Value == "[")
            {
                startsGroup = true;
                continue;
            }

            var path = Regex.Match(match.Value, @"path:\s*'([^']+)'");
            if (!path.Success)
            {
                continue;
            }

            var order = Regex.Match(match.Value, @"order:\s*'([^']+)'");
            entries.Add((path.Groups[1].Value, order.Success ? order.Groups[1].Value : "", startsGroup));
            startsGroup = false;
        }

        Assert.NotEmpty(entries);
        return entries;
    }

    /// <summary>
    /// The <c>compositeIndexes</c> array of the <c>chat-messages</c> container, sliced by balanced
    /// brackets — the array is nested, so a scan to the first <c>]</c> (which is all
    /// <see cref="EasyAuthExclusionTests"/> needs for its flat list) would stop inside it.
    /// </summary>
    private static string CompositeIndexesBlock()
    {
        var bicep = File.ReadAllText(Path.Combine(RepoRoot(), "infra", "modules", "cosmos.bicep"));

        // Scope to the chat-messages container: other containers in this file have their own
        // indexing policies, and a composite index on one of those must not satisfy this assertion.
        var resource = bicep.IndexOf("resource chatMessagesContainer", StringComparison.Ordinal);
        Assert.True(resource >= 0, "No chatMessagesContainer resource found in cosmos.bicep");

        var container = bicep[resource..];

        var start = container.IndexOf("compositeIndexes: [", StringComparison.Ordinal);
        Assert.True(
            start >= 0,
            "The chat-messages container declares no compositeIndexes, so BuildSearchSql's "
            + "multi-property ORDER BY will 400 in production.");

        var open = container.IndexOf('[', start);
        var depth = 0;
        for (var i = open; i < container.Length; i++)
        {
            if (container[i] == '[')
            {
                depth++;
            }
            else if (container[i] == ']' && --depth == 0)
            {
                return container[open..(i + 1)];
            }
        }

        Assert.Fail("Unterminated compositeIndexes array in cosmos.bicep");
        return "";
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
