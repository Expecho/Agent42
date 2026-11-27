namespace Agent42.Backend;

/// <summary>
/// Reads a downloaded attachment into memory under a hard byte cap, so a hostile or oversized file cannot exhaust memory
/// (ADR 0111). The caps are a security property: a declared <c>Content-Length</c> over the cap is refused without reading a
/// byte, and a body with no (or a lying) length is aborted the moment the running total passes the cap.
/// </summary>
public static class BoundedResponseReader
{
    private const int ChunkSize = 81920;

    /// <summary>The body of <paramref name="content"/>, or <c>null</c> when it is larger than <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]?> ReadAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        return await ReadAsync(stream, maxBytes, cancellationToken);
    }

    /// <summary>The bytes of <paramref name="stream"/>, or <c>null</c> as soon as more than <paramref name="maxBytes"/> arrive.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ChunkSize];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
