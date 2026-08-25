using System.IO.Compression;
using System.Text;

namespace Agent42.Tests;

/// <summary>
/// Builds zip archives for the uploaded-skill tests (ADR 0049) with <b>exactly</b> the entry names given.
///
/// <para>That precision is the point: <c>ZipFile.CreateFromDirectory</c> and
/// <c>ZipArchive.CreateEntryFromFile</c> both sanitize names, so a fixture built from real files could
/// never contain <c>../escape.md</c>, <c>C:\evil.md</c> or a duplicate entry name — which is to say it
/// could never exercise the guards those names exist to test.</para>
/// </summary>
public static class ZipTestData
{
    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>The entry names inside a zip, ordinally ordered so assertions are stable.</summary>
    public static IReadOnlyList<string> EntryNames(byte[] zip)
    {
        using var stream = new MemoryStream(zip, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return [.. archive.Entries.Select(e => e.FullName).OrderBy(e => e, StringComparer.Ordinal)];
    }

    /// <summary>A minimal well-formed SKILL.md body.</summary>
    public static string Frontmatter(string name, string description) =>
        $"---\nname: {name}\ndescription: {description}\n---\n\nSome guidance.\n";
}
