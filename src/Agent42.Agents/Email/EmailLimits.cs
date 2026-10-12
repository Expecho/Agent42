namespace Agent42.Agents;

/// <summary>The bounds of inbound email (ADR 0095).</summary>
public static class EmailLimits
{
    /// <summary>
    /// Cap on the prompt assembled from one email (subject + body). A forwarded thread can be long, and the prompt is
    /// persisted into the 2 MB session (ADR 0004), so the body is cut rather than the message rejected.
    /// </summary>
    public const int MaxPromptChars = 32_000;

    /// <summary>
    /// Cap on the whole <c>/api/email-in</c> request. The endpoint is public and its signature lives INSIDE the form,
    /// so the body is read before anything is authenticated; this bounds what an unauthenticated caller can make the
    /// app buffer. Three times the 10 MB per-file attachment cap, for a message with several attachments.
    /// </summary>
    public const long MaxRequestBytes = 30 * 1024 * 1024;

    /// <summary>How many addresses of a multi-address <c>recipient</c> field are tried before the message is refused.</summary>
    public const int MaxRecipients = 5;

    /// <summary>The marker appended to a prompt cut at <see cref="MaxPromptChars"/>.</summary>
    public const string TruncatedMarker = "\n\n[Email truncated]";
}
