using Agent42.Agents;

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

    private int _entered;

    /// <summary>When set, <see cref="GenerateAsync"/> throws it (ADR 0138's failure test). Reset between tests by the fixture.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, <see cref="GenerateAsync"/> waits on it first, so a test can hold an image run in flight.</summary>
    public Task? Gate { get; set; }

    /// <summary>How many generations have started, so a test can wait until one is parked on <see cref="Gate"/>.</summary>
    public int Entered => Volatile.Read(ref _entered);

    public void Reset()
    {
        Failure = null;
        Gate = null;
        Interlocked.Exchange(ref _entered, 0);
    }

    public async Task<GeneratedImage> GenerateAsync(
        string prompt, ImageInput? input, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _entered);
        if (Gate is { } gate)
        {
            await gate;
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        return input is null
            ? new GeneratedImage(TextToImageBytes, "image/png")
            : new GeneratedImage(input.Bytes, input.MediaType);
    }

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
