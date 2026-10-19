using Agent42.Agents;
using Agent42.Backend;
using Azure.Storage.Blobs;

namespace Agent42.Tests;

/// <summary>
/// The read-URL memo of <see cref="BlobAttachmentStore"/> (ADR 0104). The grain re-signs every image in history on
/// every turn; a new SAS each time changes the history segment of the request and ends every prompt-cache hit at the
/// first image. A shared-key (Azurite) client signs locally, so nothing here touches a network.
/// </summary>
public class BlobAttachmentStoreReadUrlTests
{
    private static readonly AttachmentRef Image = new("img1", "user/conv/img1", "image/png", "a.png");
    private static readonly AttachmentRef Other = new("img2", "user/conv/img2", "image/png", "b.png");

    private readonly TestTimeProvider _time = new();
    private readonly BlobAttachmentStore _store;

    public BlobAttachmentStoreReadUrlTests()
        => _store = new BlobAttachmentStore(new BlobServiceClient("UseDevelopmentStorage=true"), _time);

    [Fact]
    public async Task ReadUrl_IsReusedWhileFresh()
    {
        var first = await _store.GetReadUrlAsync(Image);
        _time.Offset = TimeSpan.FromMinutes(29);

        Assert.Equal(first, await _store.GetReadUrlAsync(Image));
    }

    [Fact]
    public async Task ReadUrl_IsRenewedPastHalfLife()
    {
        var first = await _store.GetReadUrlAsync(Image);
        _time.Offset = TimeSpan.FromMinutes(31);

        var second = await _store.GetReadUrlAsync(Image);

        Assert.NotEqual(first, second);
        // The renewed URL carries a full hour from the (shifted) now.
        var expiry = DateTimeOffset.Parse(
            System.Web.HttpUtility.ParseQueryString(second.Query)["se"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(expiry - _time.GetUtcNow(), TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
    }

    [Fact]
    public async Task ReadUrl_IsPerBlob()
    {
        var image = await _store.GetReadUrlAsync(Image);

        Assert.NotEqual(image, await _store.GetReadUrlAsync(Other));
        Assert.Contains(Other.BlobPath, (await _store.GetReadUrlAsync(Other)).AbsolutePath, StringComparison.Ordinal);
    }
}
