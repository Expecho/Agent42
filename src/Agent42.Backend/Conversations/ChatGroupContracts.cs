namespace Agent42.Backend;

// Create / rename a custom conversation group (ADR 0073). The name is trimmed and length-capped by
// the grain; a blank one is rejected by the endpoint, which is the only validation the grain does
// not own (it cannot distinguish "the user sent nothing" from "the user sent whitespace" in a way
// that is worth a status code of its own).
public record ChatGroupNameRequest(string Name);

// The user's manual ordering, as group ids in the order they should appear. Ids naming no group are
// ignored and omitted groups keep their relative order after the named ones, so a client working
// from a stale list can never drop a group.
public record ReorderChatGroupsRequest(IReadOnlyList<string> GroupIds);

// The group to file a conversation under, or null to clear it. A non-null id that names no existing
// group is a no-op rather than a clear — see IConversationIndexGrain.SetGroupAsync.
public record SetConversationGroupRequest(string? GroupId);
