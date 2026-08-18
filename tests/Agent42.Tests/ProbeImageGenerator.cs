using Agent42.Agents;
using Agent42.Agents.ImageGeneration;

namespace Agent42.Tests;

/// <summary>
/// A deterministic <see cref="IImageGenerationService"/> for the grain tests (ADR 0020). It makes the two
/// branches observable without any static/shared state:
/// <list type="bullet">
/// <item>text-to-image (null input) → a large, recognizable PNG (<see cref="TextToImageBytes"/>), so a test
/// can assert the stored image is exactly this AND — because it is large — that the bytes never entered the
/// 2 MB session (they would blow a small session-size assertion if inlined).</item>
/// <item>image-to-image (non-null input) → the input bytes ECHOED back, so a test can assert exactly which
/// image the grain fed to the edit (this turn's attachment vs. the most recent conversation image).</item>
/// </list>
/// </summary>
public sealed class ProbeImageGenerator : IImageGenerationService
{
    /// <summary>A ~256 KB PNG-signed blob: large enough that inlining it into the session is unmissable.</summary>
    public static readonly byte[] TextToImageBytes = BuildLargePng();

    public Task<GeneratedImage> GenerateAsync(
        string prompt, ImageInput? input, CancellationToken cancellationToken = default)
        => Task.FromResult(input is null
            ? new GeneratedImage(TextToImageBytes, "image/png")
            : new GeneratedImage(input.Bytes, input.MediaType));

    private static byte[] BuildLargePng()
    {
        var bytes = new byte[256 * 1024];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4E;
        bytes[3] = 0x47;
        for (var i = 4; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }
}
