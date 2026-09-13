using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Custom conversation groups (ADR 0073): the user's own named, manually ordered groupings of chats,
/// plus the one route that files a conversation into one.
///
/// <para>A group of its own rather than six more lambdas in <see cref="MapConversationEndpoints"/>:
/// that method is already close to the cognitive-complexity ceiling (S3776, threshold 15 — a build
/// error, ADR 0051), and a feature's endpoints belong beside the feature anyway (ADR 0040).</para>
///
/// <para>Every route here is behind Easy Auth — none is in the Bicep <c>excludedPaths</c> list, which
/// stays at six — and every one is scoped by the conversation-index grain key, so reaching another
/// user's groups is structurally impossible rather than merely checked.</para>
/// </summary>
public static partial class AgentApi
{
    private static void MapChatGroupEndpoints(IEndpointRouteBuilder app)
    {
        // The signed-in user's groups, in their manual order.
        app.MapGet("/api/conversation-groups", (IGrainFactory grains, HttpContext http) =>
            grains.GetGrain<IConversationIndexGrain>(http.GetUserId()).GetGroupsAsync());

        // Creates a group and returns it — the client cannot learn the minted id any other way, and it
        // needs the id immediately to file a conversation into the group it just created.
        app.MapPost("/api/conversation-groups", async (ChatGroupNameRequest request, IGrainFactory grains, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest();
            }

            var group = await grains.GetGrain<IConversationIndexGrain>(http.GetUserId()).CreateGroupAsync(request.Name);
            // Null means the per-user cap is reached. 409 rather than 400: the request is well-formed,
            // it conflicts with the state.
            return group is null ? Results.Conflict() : Results.Ok(group);
        });

        // Renames a group. Silently no-ops for an unknown id, the SetPinnedAsync convention — an
        // unlisted id must not be distinguishable from an unchanged one.
        app.MapPut("/api/conversation-groups/{groupId}", async (string groupId, ChatGroupNameRequest request, IGrainFactory grains, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest();
            }

            await grains.GetGrain<IConversationIndexGrain>(http.GetUserId()).RenameGroupAsync(groupId, request.Name);
            return Results.NoContent();
        });

        // Deletes a group and UNGROUPS its conversations. Never deletes a conversation.
        app.MapDelete("/api/conversation-groups/{groupId}", async (string groupId, IGrainFactory grains, HttpContext http) =>
        {
            await grains.GetGrain<IConversationIndexGrain>(http.GetUserId()).DeleteGroupAsync(groupId);
            return Results.NoContent();
        });

        // A distinct path rather than PUT /api/conversation-groups/order, following the house rule this
        // API already records for PUT /api/default-model: a literal segment out-ranks a {param} in the
        // same position. Group ids are server-minted so "order" could not actually collide today — the
        // rule is followed anyway so the next id-generation change cannot quietly break a route.
        app.MapPut("/api/conversation-group-order", async (ReorderChatGroupsRequest request, IGrainFactory grains, HttpContext http) =>
        {
            await grains.GetGrain<IConversationIndexGrain>(http.GetUserId()).ReorderGroupsAsync(request.GroupIds);
            return Results.NoContent();
        });

        // Files a conversation under a group, or clears it with a null groupId. Sits beside the group
        // routes rather than beside /pin so that all six of this feature's routes land in one part —
        // which is what keeps MapConversationEndpoints under the complexity ceiling.
        app.MapPut("/api/conversations/{conversationId}/group", async (string conversationId, SetConversationGroupRequest request, IGrainFactory grains, HttpContext http) =>
        {
            await grains.GetGrain<IConversationIndexGrain>(http.GetUserId()).SetGroupAsync(conversationId, request.GroupId);
            return Results.NoContent();
        });
    }
}
