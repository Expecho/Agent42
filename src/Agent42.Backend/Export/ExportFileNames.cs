using System.Text;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Every name the export writes (the download's file name, a bulk part's folders, each archive entry) is built
/// here from user- and model-authored text: a title, an upload's file name, an artifact id. Nothing from that text
/// can contribute a path separator or a <c>..</c> segment, so an entry can never land outside its folder.
/// </summary>
public static class ExportFileNames
{
    /// <summary>
    /// A title as a file-name slug: letters and digits kept (lower-cased), every other run of characters one
    /// <c>-</c>, capped at <see cref="ExportLimits.MaxSlugChars"/>. Falls back to <c>conversation</c>.
    /// </summary>
    public static string Slug(string? title)
    {
        var slug = Collapse(title ?? "", keep: char.IsLetterOrDigit, separator: '-', lower: true);
        if (slug.Length > ExportLimits.MaxSlugChars)
        {
            slug = slug[..ExportLimits.MaxSlugChars].TrimEnd('-');
        }

        return slug.Length == 0 ? "conversation" : slug;
    }

    /// <summary>
    /// An upload's file name, keeping its extension readable: letters, digits, <c>-</c> and <c>_</c> kept, a single
    /// <c>.</c> kept between them, everything else <c>_</c>. No leading dot, no <c>..</c>, no separators.
    /// </summary>
    public static string FileName(string? name)
    {
        var safe = Collapse(name ?? "", keep: c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.', separator: '_', lower: false);
        while (safe.Contains("..", StringComparison.Ordinal))
        {
            safe = safe.Replace("..", ".", StringComparison.Ordinal);
        }

        safe = safe.Trim('.', '_');
        if (safe.Length > ExportLimits.MaxFileNameChars)
        {
            safe = safe[^ExportLimits.MaxFileNameChars..].TrimStart('.', '_');
        }

        return safe.Length == 0 ? "file" : safe;
    }

    /// <summary>An id (conversation, attachment, artifact) as one path segment.</summary>
    public static string Token(string? id)
    {
        var safe = Collapse(id ?? "", keep: c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_', separator: '_', lower: false);
        return safe.Length == 0 ? "_" : safe;
    }

    /// <summary>The file extension an artifact's raw source is exported under, from its kind.</summary>
    public static string ArtifactExtension(string kind) => kind switch
    {
        ArtifactKind.Html => "html",
        ArtifactKind.Svg => "svg",
        ArtifactKind.Markdown => "md",
        _ => "txt",
    };

    /// <summary>The download name of a single-conversation export: <c>{yyyy-MM-dd}-{slug}.{extension}</c>.</summary>
    public static string Download(ConversationInfo conversation, string extension)
        => $"{conversation.UpdatedAt.UtcDateTime:yyyy-MM-dd}-{Slug(conversation.Title)}.{extension}";

    /// <summary>A conversation's folder inside a bulk part, unique by its id's first eight characters.</summary>
    public static string Folder(ConversationInfo conversation)
    {
        var id = Token(conversation.Id);
        return $"conversations/{conversation.UpdatedAt.UtcDateTime:yyyy-MM-dd}-{Slug(conversation.Title)}-{id[..Math.Min(8, id.Length)]}/";
    }

    /// <summary>An image attachment's entry, relative to its conversation's folder.</summary>
    public static string Attachment(AttachmentRef attachment)
        => $"attachments/{Token(attachment.Id)}-{FileName(attachment.FileName)}";

    /// <summary>An artifact version's entry, relative to its conversation's folder.</summary>
    public static string ArtifactVersion(ArtifactRecord artifact, int version)
        => $"artifacts/{Token(artifact.Id)}/v{version}.{ArtifactExtension(artifact.Kind)}";

    // Keeps the characters `keep` accepts and turns each run of the others into one separator, trimmed at both ends.
    private static string Collapse(string text, Func<char, bool> keep, char separator, bool lower)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (keep(c))
            {
                builder.Append(lower ? char.ToLowerInvariant(c) : c);
            }
            else if (builder.Length > 0 && builder[^1] != separator)
            {
                builder.Append(separator);
            }
        }

        return builder.ToString().Trim(separator);
    }
}
