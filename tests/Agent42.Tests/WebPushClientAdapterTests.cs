using System.Net;
using System.Security.Cryptography;
using Agent42.Backend;
using WebPush;
using PushSubscription = Agent42.Agents.Push.PushSubscription;

namespace Agent42.Tests;

/// <summary>
/// The <see cref="WebPushClientAdapter"/> is the thin isolation seam over the WebPush library (ADR 0028);
/// its only logic is translating the library's <see cref="WebPushException"/> into the seam's
/// <see cref="PushDeliveryException"/> (carrying the HTTP status the sender prunes on). That translation is
/// what <see cref="WebPushNotificationSender"/>'s 404/410-prune (AC6) rests on, so it is worth pinning here
/// even though the sender tests fake the seam out. A stubbed HttpMessageHandler returns the status; a real
/// VAPID keypair + a real P-256 subscription keypair let the library's RFC 8291 encryption succeed so the
/// send reaches the (stubbed) HTTP round-trip.
/// </summary>
public sealed class WebPushClientAdapterTests
{
    [Theory]
    [InlineData(HttpStatusCode.Gone)]        // 410 — the prune trigger
    [InlineData(HttpStatusCode.NotFound)]    // 404 — the other "subscription gone" status
    [InlineData(HttpStatusCode.InternalServerError)] // 500 — a transient failure the sender logs, not prunes
    public async Task SendAsync_TranslatesWebPushExceptionToPushDeliveryException(HttpStatusCode status)
    {
        var vapid = VapidHelper.GenerateVapidKeys();
        var adapter = new WebPushClientAdapter(
            new HttpClient(new StubHandler(status)),
            "mailto:test@agent42", vapid.PublicKey, vapid.PrivateKey);
        var subscription = new PushSubscription("https://push.example/device", NewP256dh(), NewAuth());

        var ex = await Assert.ThrowsAsync<PushDeliveryException>(
            () => adapter.SendAsync(subscription, "{\"title\":\"t\"}", CancellationToken.None));

        Assert.Equal((int)status, ex.StatusCode);
    }

    // A browser subscription's p256dh is the base64url of a raw uncompressed P-256 public point (0x04|X|Y).
    private static string NewP256dh()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdh.ExportParameters(includePrivateParameters: false);
        var point = new byte[65];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point, 1 + (32 - p.Q.X!.Length));
        p.Q.Y!.CopyTo(point, 33 + (32 - p.Q.Y!.Length));
        return Base64Url(point);
    }

    // The auth secret is 16 random bytes, base64url.
    private static string NewAuth() => Base64Url(RandomNumberGenerator.GetBytes(16));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
