using Agent42.Agents.ImageGeneration;

namespace Agent42.Tests;

/// <summary>
/// The image-generation abstraction (ADR 0020). The offline <see cref="InMemoryImageGenerator"/> stands in
/// for a real <c>/v1/images</c> endpoint (unreachable offline, like the attachment store), so what is testable
/// here is that it yields real, renderable bytes for both the text-to-image and edit shapes.
/// </summary>
public class ImageGenerationTests
{
    [Fact]
    public async Task InMemoryGenerator_TextToImage_ReturnsNonEmptyPng()
    {
        var generated = await new InMemoryImageGenerator().GenerateAsync("a cat", input: null);

        Assert.NotEmpty(generated.Bytes);
        Assert.Equal("image/png", generated.MediaType);
        // A valid PNG signature — the offline flow renders these bytes through the attachment proxy.
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], generated.Bytes[..4]);
    }

    [Fact]
    public async Task InMemoryGenerator_Edit_AlsoReturnsBytes()
    {
        var input = new ImageInput([1, 2, 3], "image/png");

        var generated = await new InMemoryImageGenerator().GenerateAsync("make it blue", input);

        Assert.NotEmpty(generated.Bytes);
        Assert.Equal("image/png", generated.MediaType);
    }
}
