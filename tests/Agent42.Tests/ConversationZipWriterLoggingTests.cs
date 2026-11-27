using System.IO.Compression;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging;

namespace Agent42.Tests;

/// <summary>cc-03: an attachment the export could not read is logged (id only) as well as listed as missing.</summary>
public class ConversationZipWriterLoggingTests
{
    [Fact]
    public async Task Export_LogsAnAttachmentItCouldNotRead()
    {
        var attachment = new AttachmentRef("att-1", "alice/c1/att-1", "image/png", "secret-name.png");
        var export = new ConversationExport(
            new ConversationInfo("c1", "private title", DateTimeOffset.UtcNow), "model", null, null,
            [new ChatTurn("user", "private conversation text", [attachment])], []);
        var manifest = new ExportManifest { ExportedAt = DateTimeOffset.UtcNow };
        var logger = new TestLogger();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await ConversationZipWriter.WriteConversationAsync(
                zip, "", export, new FailingReadStore(), "alice/c1", DateTimeOffset.UtcNow, manifest, logger, CancellationToken.None);
        }

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsType<IOException>(entry.Exception);
        Assert.Contains("att-1", entry.Message);
        Assert.DoesNotContain("private", entry.Message);
        Assert.DoesNotContain("secret-name", entry.Message);
        Assert.Single(manifest.Missing);
        Assert.Single(manifest.Conversations);
    }

    private sealed class FailingReadStore : IAttachmentStore
    {
        public Task<AttachmentContent?> OpenReadAsync(string ownerKey, string attachmentId, CancellationToken cancellationToken = default)
            => throw new IOException("blob read failed");

        public Task<AttachmentRef> SaveAsync(string ownerKey, string fileName, string mediaType, byte[] content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AttachmentRef?> GetAsync(string ownerKey, string attachmentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Uri> GetReadUrlAsync(AttachmentRef reference, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AttachmentRef> CopyAsync(AttachmentRef source, string targetOwnerKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
