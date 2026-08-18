
using Agent42.Agents;


namespace Agent42.Backend;

/// <summary>
/// The user profile: preferences read and written by the Preferences pane (ADR 0005/0021/0036).
/// </summary>
public static partial class AgentApi
{
    private static void MapUserEndpoints(IEndpointRouteBuilder app)
    {
        // The signed-in user's profile. Wrapped in Results.Json so the returned object is
        // served as application/json (the browser calls response.json()); the grain never
        // returns null — it yields the empty default when the profile was never saved.
        app.MapGet("/api/profile", async (IGrainFactory grains, HttpContext http) =>
            Results.Json(await grains.GetGrain<IUserProfileGrain>(http.GetUserId()).GetAsync()));

        // Saves the signed-in user's profile and returns the stored value (as JSON). READ-MODIFY-WRITE with
        // absent-means-keep (ADR 0021 + ADR 0005): the profile record holds several independent preferences
        // now (language, Theme, EnabledTools), and each has its own endpoint/dialog. A null field on the
        // request means "leave it as stored", so a language-only save never wipes the theme or the tool
        // default and vice-versa — the UI never sends null to mean "reset" (a stale client with old cached
        // app.js in a second tab that PUTs a language-only save must not reset the saved theme). Tool keys
        // are sanitized server-side.
        app.MapPut("/api/profile", async (ProfileUpdateRequest request, IGrainFactory grains, HttpContext http) =>
        {
            var grain = grains.GetGrain<IUserProfileGrain>(http.GetUserId());
            var current = await grain.GetAsync();
            var saved = await grain.SaveAsync(current with
            {
                PreferredLanguage = request.PreferredLanguage ?? current.PreferredLanguage,
                Theme = request.Theme ?? current.Theme,
                EnabledTools = request.EnabledTools is null ? current.EnabledTools : ToolCatalog.Sanitize(request.EnabledTools),
                SuggestFollowUps = request.SuggestFollowUps ?? current.SuggestFollowUps,
            });
            return Results.Json(saved);
        });

        // ————— model providers (per-user overlay, ADR 0018) —————
    }
}
