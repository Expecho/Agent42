using Agent42.Agents;
using OpenAI.Images;
using Agent42.Agents.ImageGeneration;

namespace Agent42.Backend;

/// <summary>
/// Production <see cref="IImageGenerationService"/> (ADR 0020) over the OpenAI SDK <see cref="ImageClient"/>:
/// text-to-image via <c>GenerateImageAsync</c>, image-to-image via <c>GenerateImageEditAsync</c>. The grain
/// persists the returned BYTES to blob storage and never inlines them into the 2 MB session (ADR 0004).
///
/// <para><b>Do NOT set <c>ResponseFormat</c>.</b> The current OpenAI image model <c>gpt-image-1</c> REJECTS a
/// <c>response_format</c> parameter with <c>HTTP 400 unknown_parameter</c> (it always returns base64), while
/// the older <c>dall-e-*</c> models default to a hosted URL. So we send no <c>response_format</c> and resolve
/// the result either way in <see cref="ResolveBytesAsync"/>: prefer the inline bytes (<c>gpt-image-1</c>), else
/// download the URL (<c>dall-e</c>). Sending <c>response_format</c> unconditionally broke every <c>gpt-image-1</c>
/// endpoint (observed on the wire).</para>
///
/// <para>The <see cref="ImageClient"/> is built the SAME transport way as
/// <see cref="MultiProviderChatClientFactory.Provider.OpenAICompatible"/> — the SDK's own
/// <c>OpenAIClient</c>, NOT an <c>IHttpClientFactory</c> client — because a named <c>HttpClient</c> silently
/// inherits <c>AddServiceDefaults</c>' 3-retry resilience pipeline, and <c>AgentGrain</c> is non-reentrant, so
/// a retried image call would multiply the stall on the user's turn (see the "named HttpClient inherits the
/// retry pipeline" gotcha). The endpoint still comes only from the provider's config — SSRF invariant intact.</para>
///
/// <para>The Step-0 spike (pinned OpenAI 2.12.0) confirmed this surface; the LIVE <c>/v1/images</c> exchange
/// is not reachable offline (<see cref="InMemoryImageGenerator"/> stands in), so this class is exercised by
/// prod smoke, not a unit test — honestly out of unit scope, like the blob SAS path (ADR 0004).</para>
/// </summary>
public sealed class OpenAIImageGenerator(ImageClient client) : IImageGenerationService
{
    // Shared, for the URL-response fallback only (dall-e-style models return a short-lived hosted URL, not
    // inline bytes). A plain static HttpClient — NOT a named/DI one — so it does not inherit the
    // AddServiceDefaults retry pipeline (gotcha) and does not exhaust sockets. Plain GET, no auth header, so it
    // never carries the app's managed identity. The URL comes from the user's OWN configured image provider
    // (config, not a model/request choice), so it stays off the fetch_web_page SSRF path (ADR 0018).
    private static readonly HttpClient Http = new();

    public async Task<Agent42.Agents.ImageGeneration.GeneratedImage> GenerateAsync(
        string prompt, ImageInput? input, CancellationToken cancellationToken = default)
    {
        OpenAI.Images.GeneratedImage image;
        if (input is null)
        {
            // Text-to-image. No ResponseFormat (see the class remarks — gpt-image-1 400s on it).
            var result = await client.GenerateImageAsync(prompt, new ImageGenerationOptions(), cancellationToken);
            image = result.Value;
        }
        else
        {
            // Image-to-image (edit): the SDK wants a stream + a filename (media type is inferred from it).
            using var stream = new MemoryStream(input.Bytes);
            var edited = await client.GenerateImageEditAsync(
                stream, "input" + ImageMediaTypes.ExtensionFor(input.MediaType), prompt,
                new ImageEditOptions(), cancellationToken);
            image = edited.Value;
        }

        var bytes = await ResolveBytesAsync(image, cancellationToken);
        // Sniff the RETURNED format rather than assuming PNG: an OpenAI-compatible endpoint may return
        // JPEG/WebP, and a wrong Content-Type would then be stored + served (critic Minor-1).
        return new Agent42.Agents.ImageGeneration.GeneratedImage(bytes, ImageMediaTypes.DetectMediaType(bytes));
    }

    // gpt-image-1 returns inline b64 bytes; dall-e defaults to a hosted URL. Prefer bytes; else download the
    // URL (see the class remarks for why no ResponseFormat is sent, which is what makes both shapes possible).
    private static async Task<byte[]> ResolveBytesAsync(
        OpenAI.Images.GeneratedImage image, CancellationToken cancellationToken)
    {
        if (image.ImageBytes is not null)
        {
            return image.ImageBytes.ToArray();
        }

        if (image.ImageUri is not null)
        {
            return await Http.GetByteArrayAsync(image.ImageUri, cancellationToken);
        }

        throw new InvalidOperationException(
            "The image endpoint returned neither inline image bytes nor a URL.");
    }
}
