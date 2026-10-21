using System.Net;
using System.Text;
using System.Text.Json;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// Signal's JSON-RPC client (ADR 0111, phase 3, SC6) over a stub handler: request shapes, the error mapping, and the
/// SSE reader.
/// </summary>
public class SignalRpcTests
{
    private const string Number = "+31600000000";
    private const string Aci = "aaaaaaaa-0000-4000-8000-000000000002";
    private const string GroupId = "R3JvdXBJZA==";

    private sealed class StubDaemon : ISignalDaemon
    {
        public Task<Uri> EnsureRunningAsync(CancellationToken cancellationToken) => Task.FromResult(new Uri("http://127.0.0.1:8086/"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Rpc(string result) => Json($$"""{"jsonrpc":"2.0","result":{{result}},"id":"1"}""");

    private static HttpResponseMessage RpcError(int code, string message, string? data = null)
        => Json($$"""{"jsonrpc":"2.0","error":{"code":{{code}},"message":"{{message}}"{{(data is null ? "" : ",\"data\":" + data)}}},"id":"1"}""");

    private static (SignalRpcClient Client, StubHandler Handler) Client(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new SignalRpcClient(new Factory(handler), new StubDaemon()), handler);
    }

    private static JsonElement SentBody(StubHandler handler) => JsonDocument.Parse(handler.Calls[^1].Body!).RootElement;

    [Fact]
    public async Task ASendToAPerson_PostsRecipientArray_WithStylesAttachmentsAndQuote()
    {
        var (client, handler) = Client((_, _) => Rpc($$"""{"timestamp":1700000000999,"results":[{"recipientAddress":{"uuid":"{{Aci}}"},"type":"SUCCESS"}]}"""));

        var outcome = await client.SendAsync(
            Number, Aci, "hi there", ["0:2:BOLD"], ["data:text/plain;filename=a.txt;base64,aGk="], 1700000000123, Aci, CancellationToken.None);

        Assert.True(outcome.Result.Success);
        Assert.Equal("1700000000999", outcome.Result.MessageId);
        var call = handler.Calls[0];
        Assert.Equal(HttpMethod.Post, call.Request.Method);
        Assert.Equal("http://127.0.0.1:8086/api/v1/rpc", call.Request.RequestUri!.ToString());
        Assert.Equal("application/json", call.Request.Content!.Headers.ContentType!.MediaType);
        var body = SentBody(handler);
        Assert.Equal("2.0", body.GetProperty("jsonrpc").GetString());
        Assert.Equal("send", body.GetProperty("method").GetString());
        var p = body.GetProperty("params");
        Assert.Equal(Number, p.GetProperty("account").GetString());
        Assert.Equal(Aci, Assert.Single(p.GetProperty("recipient").EnumerateArray()).GetString());
        Assert.False(p.TryGetProperty("groupId", out _));
        Assert.Equal("hi there", p.GetProperty("message").GetString());
        Assert.Equal("0:2:BOLD", Assert.Single(p.GetProperty("textStyles").EnumerateArray()).GetString());
        Assert.Equal("data:text/plain;filename=a.txt;base64,aGk=", Assert.Single(p.GetProperty("attachments").EnumerateArray()).GetString());
        Assert.Equal(1700000000123, p.GetProperty("quoteTimestamp").GetInt64());
        Assert.Equal(Aci, p.GetProperty("quoteAuthor").GetString());
    }

    [Fact]
    public async Task ASendToAGroup_PostsGroupId_AndOmitsEmptyOptionals()
    {
        var (client, handler) = Client((_, _) => Rpc("""{"timestamp":5,"results":[]}"""));

        var outcome = await client.SendAsync(Number, GroupId, "x", null, null, null, null, CancellationToken.None);

        Assert.Equal("5", outcome.Result.MessageId);
        var p = SentBody(handler).GetProperty("params");
        Assert.Equal(GroupId, p.GetProperty("groupId").GetString());
        Assert.False(p.TryGetProperty("recipient", out _));
        Assert.False(p.TryGetProperty("textStyles", out _));
        Assert.False(p.TryGetProperty("attachments", out _));
        Assert.False(p.TryGetProperty("quoteTimestamp", out _));
    }

    [Fact]
    public async Task TextStyleOffsets_FromTheConverter_AreSentVerbatim_AfterAnEmoji()
    {
        var (text, styles) = SignalText.FromMarkdown("\U0001F600 **hi**");
        var (client, handler) = Client((_, _) => Rpc("""{"timestamp":1,"results":[]}"""));

        await client.SendAsync(Number, Aci, text, styles, null, null, null, CancellationToken.None);

        Assert.Equal("3:2:BOLD", SentBody(handler).GetProperty("params").GetProperty("textStyles")[0].GetString());
    }

    [Fact]
    public async Task Typing_PostsSendTyping_AndStopOnlyWhenAsked()
    {
        var (client, handler) = Client((_, _) => Rpc("{}"));

        await client.SendTypingAsync(Number, Aci, stop: false, CancellationToken.None);
        Assert.Equal("sendTyping", SentBody(handler).GetProperty("method").GetString());
        Assert.False(SentBody(handler).GetProperty("params").TryGetProperty("stop", out _));

        await client.SendTypingAsync(Number, GroupId, stop: true, CancellationToken.None);
        Assert.True(SentBody(handler).GetProperty("params").GetProperty("stop").GetBoolean());
        Assert.Equal(GroupId, SentBody(handler).GetProperty("params").GetProperty("groupId").GetString());
    }

    [Fact]
    public async Task GetAttachment_DecodesBase64_AndAsksWithTheChat()
    {
        var (client, handler) = Client((_, _) => Rpc($$"""{"data":"{{Convert.ToBase64String("hello"u8)}}"}"""));

        var bytes = await client.GetAttachmentAsync(Number, "att1", Aci, 100, CancellationToken.None);

        Assert.Equal("hello"u8.ToArray(), bytes);
        var p = SentBody(handler).GetProperty("params");
        Assert.Equal("getAttachment", SentBody(handler).GetProperty("method").GetString());
        Assert.Equal("att1", p.GetProperty("id").GetString());
        Assert.Equal(Aci, p.GetProperty("recipient")[0].GetString());
    }

    [Fact]
    public async Task GetAttachment_ReturnsNull_WhenOverTheCap_OrFailed_OrNotBase64()
    {
        var big = Convert.ToBase64String(new byte[1000]);
        var (over, _) = Client((_, _) => Rpc($$"""{"data":"{{big}}"}"""));
        var (failed, _) = Client((_, _) => RpcError(-1, "no such attachment"));
        var (garbage, _) = Client((_, _) => Rpc("""{"data":"!!!not base64!!!"}"""));

        Assert.Null(await over.GetAttachmentAsync(Number, "a", Aci, 999, CancellationToken.None));
        Assert.NotNull(await over.GetAttachmentAsync(Number, "a", Aci, 1000, CancellationToken.None));
        Assert.Null(await failed.GetAttachmentAsync(Number, "a", Aci, 1000, CancellationToken.None));
        Assert.Null(await garbage.GetAttachmentAsync(Number, "a", Aci, 1000, CancellationToken.None));
    }

    [Fact]
    public async Task RateLimit_MapsToRateLimited_WithRetryAfter_WhenGiven()
    {
        var (withHint, _) = Client((_, _) => RpcError(-5, "rate limited", """{"retryAfterSeconds":42}"""));
        var (without, _) = Client((_, _) => RpcError(-5, "rate limited"));

        var hinted = await withHint.SendAsync(Number, Aci, "x", null, null, null, null, CancellationToken.None);
        var plain = await without.SendAsync(Number, Aci, "x", null, null, null, null, CancellationToken.None);

        Assert.Equal((ChatSendErrorKind.RateLimited, TimeSpan.FromSeconds(42)), (hinted.Result.ErrorKind, hinted.Result.RetryAfter));
        Assert.Equal((ChatSendErrorKind.RateLimited, null), (plain.Result.ErrorKind, plain.Result.RetryAfter));
        Assert.False(hinted.ProofRequired);
    }

    [Fact]
    public async Task AProofToken_MapsToRateLimited_AndFlagsProofRequired()
    {
        var (client, _) = Client((_, _) => Rpc($$"""{"timestamp":1,"results":[{"recipientAddress":{"uuid":"{{Aci}}"},"type":"RATE_LIMIT_FAILURE","token":"abc-token"}]}"""));

        var outcome = await client.SendAsync(Number, Aci, "x", null, null, null, null, CancellationToken.None);

        Assert.False(outcome.Result.Success);
        Assert.Equal(ChatSendErrorKind.RateLimited, outcome.Result.ErrorKind);
        Assert.True(outcome.ProofRequired);
    }

    [Theory]
    [InlineData("UNREGISTERED_FAILURE", ChatSendErrorKind.NotFound)]
    [InlineData("NETWORK_FAILURE", ChatSendErrorKind.Transient)]
    [InlineData("IDENTITY_FAILURE", ChatSendErrorKind.Unknown)]
    public async Task AFailedRecipientType_MapsToItsKind(string type, ChatSendErrorKind expected)
    {
        var (client, _) = Client((_, _) => Rpc($$"""{"timestamp":1,"results":[{"type":"{{type}}"}]}"""));

        var outcome = await client.SendAsync(Number, Aci, "x", null, null, null, null, CancellationToken.None);

        Assert.Equal((false, expected, false), (outcome.Result.Success, outcome.Result.ErrorKind, outcome.ProofRequired));
    }

    [Fact]
    public async Task AGroupSend_IsSuccessful_WhenAtLeastOneMemberGotIt()
    {
        var (client, _) = Client((_, _) => Rpc("""{"timestamp":9,"results":[{"type":"SUCCESS"},{"type":"UNREGISTERED_FAILURE"}]}"""));

        var outcome = await client.SendAsync(Number, GroupId, "x", null, null, null, null, CancellationToken.None);

        Assert.Equal((true, "9"), (outcome.Result.Success, outcome.Result.MessageId));
    }

    [Fact]
    public async Task IoError_AndServerErrors_AndTransportFailures_AreTransient()
    {
        var (io, _) = Client((_, _) => RpcError(-3, "IO error"));
        var (http500, _) = Client((_, _) => Json("oops", HttpStatusCode.InternalServerError));
        var (down, _) = Client((_, _) => throw new HttpRequestException("connection refused"));

        foreach (var client in new[] { io, http500, down })
        {
            var outcome = await client.SendAsync(Number, Aci, "x", null, null, null, null, CancellationToken.None);
            Assert.Equal(ChatSendErrorKind.Transient, outcome.Result.ErrorKind);
        }
    }

    [Fact]
    public async Task AnUnknownAccount_IsFlagged_NotRetried()
    {
        var (client, _) = Client((_, _) => RpcError(-32602, "Specified account does not exist"));
        var (other, _) = Client((_, _) => RpcError(-32602, "Method requires account parameter"));

        var outcome = await client.SendAsync(Number, Aci, "x", null, null, null, null, CancellationToken.None);
        var otherOutcome = await other.SendAsync(Number, Aci, "x", null, null, null, null, CancellationToken.None);

        Assert.True(outcome.IsUnknownAccount);
        Assert.False(outcome.Result.Success);
        Assert.False(otherOutcome.IsUnknownAccount);
    }

    [Fact]
    public async Task ACaptchaDemand_IsAUserErrorNamingTheCaptcha()
    {
        var (client, handler) = Client((_, _) => RpcError(-1, "Captcha required for verification, use --captcha CAPTCHA"));
        var (other, _) = Client((_, _) => RpcError(-1, "Before requesting voice verification you need to request SMS verification"));

        var result = await client.RegisterAsync(Number, null, voice: false, CancellationToken.None);
        var voice = await other.RegisterAsync(Number, "signalcaptcha://x", voice: true, CancellationToken.None);

        Assert.True(result.IsCaptchaRequired);
        Assert.False(voice.IsCaptchaRequired);
        Assert.Equal("register", SentBody(handler).GetProperty("method").GetString());
        Assert.False(SentBody(handler).GetProperty("params").TryGetProperty("captcha", out _));
    }

    [Fact]
    public async Task RegistrationCalls_CarryTheirParameters()
    {
        var (client, handler) = Client((_, _) => Rpc("null"));

        await client.RegisterAsync(Number, "signalcaptcha://abc", voice: true, CancellationToken.None);
        var register = SentBody(handler).GetProperty("params");
        Assert.Equal(("signalcaptcha://abc", true), (register.GetProperty("captcha").GetString(), register.GetProperty("voice").GetBoolean()));

        await client.VerifyAsync(Number, "123456", "9876", CancellationToken.None);
        var verify = SentBody(handler);
        Assert.Equal("verify", verify.GetProperty("method").GetString());
        Assert.Equal(("123456", "9876"), (verify.GetProperty("params").GetProperty("verificationCode").GetString(), verify.GetProperty("params").GetProperty("pin").GetString()));

        await client.UnregisterAsync(Number, CancellationToken.None);
        Assert.Equal("unregister", SentBody(handler).GetProperty("method").GetString());
        await client.DeleteLocalAccountDataAsync(Number, CancellationToken.None);
        Assert.Equal("deleteLocalAccountData", SentBody(handler).GetProperty("method").GetString());
    }

    [Fact]
    public async Task ListAccounts_ParsesNumberAndAci_AndNullMeansTheCallFailed()
    {
        var (client, handler) = Client((_, _) => Rpc($$"""[{"number":"{{Number}}","aci":"{{Aci}}"},{"number":"+31611111111"}]"""));
        var (failed, _) = Client((_, _) => Json("x", HttpStatusCode.BadGateway));

        var accounts = await client.ListAccountsAsync(CancellationToken.None);

        Assert.Equal("listAccounts", SentBody(handler).GetProperty("method").GetString());
        Assert.Equal([new SignalAccount(Number, Aci), new SignalAccount("+31611111111", null)], accounts);
        Assert.Null(await failed.ListAccountsAsync(CancellationToken.None));
    }

    private static async Task<List<string>> ReadAll(SignalRpcClient client)
    {
        var events = new List<string>();
        await foreach (var evt in client.ReadEventsAsync(Number, CancellationToken.None))
        {
            events.Add(evt.GetProperty("n").GetInt32().ToString());
        }

        return events;
    }

    [Fact]
    public async Task TheEventStream_YieldsReceiveEvents_SkipsCommentsAndOtherEvents()
    {
        const string Sse = ": keepalive\n\n"
            + "event: receive\ndata: {\"n\":1}\n\n"
            + ": another comment\n"
            + "event: something-else\ndata: {\"n\":99}\n\n"
            + "id: 7\nevent: receive\ndata:{\"n\":\ndata:2}\n\n"
            + "event: receive\ndata: not json\n\n";
        var (client, handler) = Client((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Sse, Encoding.UTF8, "text/event-stream"),
        });

        var events = await ReadAll(client);

        Assert.Equal(["1", "2"], events);
        var request = handler.Calls[0].Request;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/events", request.RequestUri!.AbsolutePath);
        Assert.Contains("account=%2B31600000000", request.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEventsOpenWith400_IsAnAuthFault_AndOtherStatusesThrow()
    {
        var (unknown, _) = Client((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var (broken, _) = Client((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));

        await Assert.ThrowsAsync<ChatAuthException>(() => ReadAll(unknown));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => ReadAll(broken));
        Assert.DoesNotContain(Number, error.Message, StringComparison.Ordinal);
    }
}
