using System.ComponentModel;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// <c>send_push_notification</c> for one fleet task (ADR 0144): the chat's tool (ADR 0075) without a conversation. The
/// title and body are model-authored, so they are sanitized and capped by the chat's own
/// <see cref="PushToolLimits.SanitizeNotificationText"/>. The budget (<see cref="PushToolLimits.MaxPushesPerTurn"/>, per TASK
/// here) lives on this instance and is spent only on a send. The tap opens the task (<see cref="PushMessage.Url"/>). Web Push
/// only: the chat-app home chat (ADR 0111) is out of scope. The text is never logged.
/// </summary>
public sealed class FleetPushTools
{
    public const string Name = "send_push_notification";

    /// <summary>Notifications one fleet task may send, all agents together. The chat's per-turn cap, applied per task.</summary>
    public const int MaxPushesPerTask = PushToolLimits.MaxPushesPerTurn;

    private readonly IPushNotificationSender _sender;
    private readonly IGrainFactory _grains;
    private readonly string _userId;
    private readonly string _taskId;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private int _sent;

    public FleetPushTools(IPushNotificationSender sender, IGrainFactory grains, string userId, string taskId, ILogger logger)
    {
        _sender = sender;
        _grains = grains;
        _userId = userId;
        _taskId = taskId;
        _logger = logger;
        Tool = AIFunctionFactory.Create(SendAsync, Name,
            "Send a push notification to the user's devices, when the task asks you to notify the user about something. "
            + "Put the news in the title. Tapping it opens this task. Delivery is best-effort with no receipt. "
            + $"At most {MaxPushesPerTask} per task, for all agents together.");
    }

    public AIFunction Tool { get; }

    /// <summary>The page a tap opens: the task on <c>/fleet/</c> (the deep link ADR 0115 introduced).</summary>
    public static string TaskUrl(string taskId) => $"/fleet/?task={Uri.EscapeDataString(taskId)}";

    private async Task<string> SendAsync(
        [Description("The notification title, read first on a locked screen with no context: 'Report ready', not 'Update'. At most 100 characters.")] string title,
        [Description("One or two sentences of detail. At most 300 characters, plain text.")] string body,
        CancellationToken cancellationToken)
    {
        var cleanTitle = PushToolLimits.SanitizeNotificationText(title, PushToolLimits.MaxTitleLength);
        var cleanBody = PushToolLimits.SanitizeNotificationText(body, PushToolLimits.MaxBodyLength);
        if (cleanTitle.Length == 0 || cleanBody.Length == 0)
        {
            return "That notification had no usable text left after cleaning, so it was not sent. Both the title and the "
                + "body must contain ordinary readable characters.";
        }

        var devices = await _grains.GetGrain<IUserPushGrain>(_userId).GetAllAsync();
        if (devices.Count == 0)
        {
            return "The user has no devices subscribed to notifications, so nothing was sent. Put what you wanted to "
                + "tell them in the task's result instead.";
        }

        // Counted on the send only, as in the chat: unusable text spends nothing. Agents of one task run concurrently.
        lock (_gate)
        {
            if (_sent >= MaxPushesPerTask)
            {
                return $"This task has sent {MaxPushesPerTask} notifications, the limit. Put anything else in the result.";
            }

            _sent++;
        }

        await _sender.SendAsync(_userId, new PushMessage(cleanTitle, cleanBody, "", TaskUrl(_taskId)), cancellationToken);

        // Never the title or the body: model-authored text about what the user is doing (the PushTool_DoesNotLogNotificationText rule).
        _logger.LogInformation("Fleet task {TaskId} queued a push notification to {Devices} device(s)", _taskId, devices.Count);
        return $"Notification queued to {devices.Count} device(s), titled \"{cleanTitle}\". There is no delivery receipt.";
    }
}
