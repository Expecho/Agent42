namespace Agent42.Agents;

/// <summary>
/// Generates an image from a prompt (ADR 0020): text-to-image when <paramref name="input"/> is null,
/// image-to-image (edit) when it is supplied. This is the surface a picked image-generation model
/// (<see cref="ModelCapabilities.SupportsImageGeneration"/>) runs on — a different API from chat
/// (OpenAI-compatible <c>/v1/images</c>, not <c>/v1/chat/completions</c>).
///
/// <para>Named <c>IImageGenerationService</c>, NOT <c>IImageGenerator</c>, on purpose: Microsoft.Extensions.AI
/// 1.13 ships its own <c>Microsoft.Extensions.AI.IImageGenerator</c>, and a same-named type here would be an
/// ambiguous reference (CS0104) in every Backend file that imports both namespaces (the Step-0 spike hit
/// exactly that on <c>ImageGenerationOptions</c>). We keep a minimal in-house abstraction — mirroring
/// <see cref="IAttachmentStore"/>'s real+in-memory split — rather than adopt the heavier M.E.AI request/response
/// shape.</para>
///
/// <para>Production is <c>OpenAIImageGenerator</c> (Backend, over the OpenAI SDK <c>ImageClient</c>); local dev
/// and tests use <see cref="InMemoryImageGenerator"/>, which returns a canned PNG — the real <c>/v1/images</c>
/// contract is Step-0 spike-verified, not reachable offline (same limitation ADR 0004 documents for images).</para>
///
/// <para><b>Egress (ADR 0013):</b> a production implementation sends the prompt — and for an edit, the
/// <see cref="ImageInput"/> bytes (an actual uploaded/earlier photo) — to a user-configured endpoint OUTSIDE
/// the Entra tenant. That is the headline consequence recorded in ADR 0020.</para>
/// </summary>
public interface IImageGenerationService
{
    /// <summary>
    /// Generates one image for <paramref name="prompt"/>. A null <paramref name="input"/> is text-to-image;
    /// a non-null one is an edit against those bytes. Returns the generated image's bytes + media type — the
    /// caller (the grain) writes them to blob storage and never inlines them into the session (ADR 0004).
    /// </summary>
    Task<GeneratedImage> GenerateAsync(
        string prompt, ImageInput? input, CancellationToken cancellationToken = default);
}

/// <summary>A generated image: its raw bytes and media type. The bytes go to blob storage, never the session.</summary>
public sealed record GeneratedImage(byte[] Bytes, string MediaType);

/// <summary>The input image for an edit (image-to-image): raw bytes read from the attachment store + media type.</summary>
public sealed record ImageInput(byte[] Bytes, string MediaType);

/// <summary>
/// Image media-type helpers shared by the grain and the prod generator (ADR 0020) — one home for the
/// media-type ⇆ extension mapping and byte-signature sniffing, so the two sides can never drift.
/// </summary>
public static class ImageMediaTypes
{
    /// <summary>A file extension (with the dot) for a media type; defaults to <c>.png</c>.</summary>
    public static string ExtensionFor(string mediaType) => mediaType switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".png",
    };

    /// <summary>
    /// The media type of image bytes, by magic-number signature — so an OpenAI-compatible endpoint that
    /// returns JPEG/WebP/GIF (rather than the OpenAI default PNG) is stored and served with the RIGHT
    /// <c>Content-Type</c>, not a hardcoded <c>image/png</c>. Defaults to PNG (the OpenAI <c>/v1/images</c> default).
    /// </summary>
    public static string DetectMediaType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        if (bytes.Length >= 3 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return "image/gif";
        }

        return "image/png";
    }
}

/// <summary>
/// In-memory <see cref="IImageGenerationService"/> for local dev (DevHost) and tests, where no real
/// <c>/v1/images</c> endpoint is reachable (the same offline limitation as <see cref="InMemoryAttachmentStore"/>).
/// Returns a tiny canned PNG for every call — enough to exercise the store/display/history flow end to end;
/// it never inspects the prompt or input. Stateless, so a plain instance suffices (nothing to share across the
/// endpoint↔grain boundary the way the attachment store must).
/// </summary>
public sealed class InMemoryImageGenerator : IImageGenerationService
{
    // A 1×1 PNG — the smallest valid image, so the offline flow has real, renderable bytes without shipping a
    // large asset. Decoded once; the returned array is copied per call so a caller can never mutate the source.
    private static readonly byte[] CannedPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public Task<GeneratedImage> GenerateAsync(
        string prompt, ImageInput? input, CancellationToken cancellationToken = default)
        => Task.FromResult(new GeneratedImage([.. CannedPng], "image/png"));
}
