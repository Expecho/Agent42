
using Agent42.Agents;


namespace Agent42.Backend;

/// <summary>
/// Server-side text-to-speech (ADR 0038): resolves the factory optionally and 409s when no speech model is configured.
/// </summary>
public static partial class AgentApi
{
    private static void MapSpeechEndpoints(IEndpointRouteBuilder app)
    {
        // Synthesizes speech audio for the given text via the user's default speech model (ADR 0038), so the
        // voice conversation (ADR 0037) can speak the reply with a server-side neural voice instead of the
        // browser's speechSynthesis. Resolves the per-user factory (like the grain does) and returns the audio
        // bytes; 409 when no speech model is configured, so the browser falls back. Conversation-independent —
        // it only synthesizes text, so it takes no conversationId. DisableAntiforgery: the app is Easy-Auth
        // gated, not cookie/forms (same as the attachment upload).
        // IServiceProvider (NOT IUserChatClientFactoryProvider) is injected deliberately, mirroring
        // /api/config-health: MapAgentApi infers every endpoint's params up front, and a param a host does not
        // register would be treated as a body and break routing for the WHOLE host — some test hosts don't
        // register the factory provider. Resolving it optionally 409s cleanly where it is absent instead.
        app.MapPost("/api/speech", async (SpeechRequest request, IServiceProvider services, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.Text("No text to synthesize.", "text/plain", statusCode: StatusCodes.Status400BadRequest);
            }

            var factories = services.GetService<IUserChatClientFactoryProvider>();
            var speech = factories is null ? null : (await factories.GetForUserAsync(http.GetUserId(), ct)).GetSpeechService();
            if (speech is null)
            {
                return Results.Text("No speech model configured.", "text/plain", statusCode: StatusCodes.Status409Conflict);
            }

            var audio = await speech.SynthesizeAsync(request.Text, ct);
            return Results.File(audio.Bytes, audio.MediaType);
        }).DisableAntiforgery();

        // ————— Web Push subscriptions (ADR 0028) —————
    }
}
