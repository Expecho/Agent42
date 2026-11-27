using System.Net;
using Agent42.Backend;

namespace Agent42.Tests;

public sealed class BoundedResponseReaderTests
{
    private const long Cap = 100_000; // spans more than one 81,920-byte chunk

    [Fact]
    public async Task BoundedResponseReader_UnderCap_ReturnsTheBytes()
    {
        var body = new byte[Cap - 1];
        Random.Shared.NextBytes(body);

        var result = await BoundedResponseReader.ReadAsync(new ByteArrayContent(body), Cap, CancellationToken.None);

        Assert.Equal(body, result);
    }

    [Fact]
    public async Task BoundedResponseReader_ExactlyAtCap_ReturnsTheBytes()
    {
        var result = await BoundedResponseReader.ReadAsync(new ByteArrayContent(new byte[Cap]), Cap, CancellationToken.None);

        Assert.Equal((int)Cap, result?.Length);
    }

    [Fact]
    public async Task BoundedResponseReader_OneOverCap_ReturnsNull()
    {
        var result = await BoundedResponseReader.ReadAsync(new ByteArrayContent(new byte[Cap + 1]), Cap, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task BoundedResponseReader_ContentLengthOverCap_ReturnsNullWithoutReading()
    {
        var content = new ThrowingContent(Cap + 1);

        var result = await BoundedResponseReader.ReadAsync(content, Cap, CancellationToken.None);

        Assert.Null(result);
        Assert.False(content.StreamRequested);
    }

    [Fact]
    public async Task BoundedResponseReader_NoContentLengthAndStreamOverCap_AbortsMidStream()
    {
        await using var stream = new MemoryStream(new byte[Cap * 3]);

        var result = await BoundedResponseReader.ReadAsync(stream, Cap, CancellationToken.None);

        Assert.Null(result);
        Assert.True(stream.Position < stream.Length, "the reader must stop at the cap, not drain the body");
    }

    private sealed class ThrowingContent : HttpContent
    {
        public ThrowingContent(long declaredLength) => Headers.ContentLength = declaredLength;

        public bool StreamRequested { get; private set; }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            StreamRequested = true;
            throw new InvalidOperationException("body must not be read");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            StreamRequested = true;
            throw new InvalidOperationException("body must not be read");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength ?? 0;
            return true;
        }
    }
}
