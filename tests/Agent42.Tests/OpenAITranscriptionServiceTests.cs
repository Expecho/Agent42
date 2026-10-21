using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Agent42.Agents;
using Agent42.Backend;
using OpenAI;

namespace Agent42.Tests;

/// <summary>
/// <see cref="OpenAITranscriptionService"/> against a stub transport wired into the SDK client (the same seam
/// production uses, minus the network): the request shape, the transcript coming back, and a provider refusal becoming
/// a curated <see cref="SpeechTranscriptionException"/> (ADR 0113).
/// </summary>
public sealed class OpenAITranscriptionServiceTests
{
    private static (OpenAITranscriptionService Service, RecordingHttpHandler Handler) Create(Func<RecordingHttpHandler.Call, HttpResponseMessage> responder)
    {
        var handler = new RecordingHttpHandler { Responder = responder };
        var client = new OpenAIClient(
            new ApiKeyCredential("sk-test"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri("https://transcribe.example/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            });
        return (new OpenAITranscriptionService(client.GetAudioClient("whisper-1")), handler);
    }

    [Fact]
    public async Task Transcribe_PostsMultipartToTheTranscriptionsEndpoint_AndReturnsTheText()
    {
        var (service, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"text":"hello from a voice note"}""", Encoding.UTF8, "application/json"),
        });

        var transcript = await service.TranscribeAsync([1, 2, 3, 4], "note.ogg", "audio/ogg");

        Assert.Equal("hello from a voice note", transcript);
        var call = Assert.Single(handler.Recorded);
        Assert.Equal("POST", call.Method);
        Assert.Equal("https://transcribe.example/v1/audio/transcriptions", call.Url);
        Assert.Equal("Bearer sk-test", call.Authorization);
        var body = call.Body.Replace("\"", "");
        Assert.Contains("name=file", body, StringComparison.Ordinal);
        Assert.Contains("name=model", body, StringComparison.Ordinal);
        Assert.Contains("filename=note.ogg", body, StringComparison.Ordinal);
        Assert.Contains("whisper-1", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transcribe_A400_BecomesACuratedException_NotTheProvidersText()
    {
        var (service, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"provider detail with sk-leak"}}""", Encoding.UTF8, "application/json"),
        });

        var ex = await Assert.ThrowsAsync<SpeechTranscriptionException>(() => service.TranscribeAsync([1], "note.ogg", "audio/ogg"));

        Assert.DoesNotContain("sk-leak", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider detail", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transcribe_A500_Propagates()
    {
        var (service, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        await Assert.ThrowsAsync<ClientResultException>(() => service.TranscribeAsync([1], "note.mp3", "audio/mpeg"));
    }

    [Fact]
    public async Task SingleChatClientFactory_ReturnsTheSuppliedTranscriber_AndNullWithout()
    {
        var transcriber = new InMemorySpeechTranscriptionService("canned");
        var with = new SingleChatClientFactory(new FakeChatClient(), transcriber: transcriber);
        var without = new SingleChatClientFactory(new FakeChatClient());

        Assert.Same(transcriber, with.GetTranscriptionService());
        Assert.NotEqual("", with.DefaultTranscriptionModelId);
        Assert.Null(without.GetTranscriptionService());
        Assert.Equal("", without.DefaultTranscriptionModelId);
        Assert.Equal("canned", await with.GetTranscriptionService()!.TranscribeAsync([1], "a.mp3", "audio/mpeg"));
    }
}
