using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Agent42.Agents.Scheduling;

namespace Agent42.Agents.Conversations;

/// <summary>
/// The scheduling tools (<c>schedule_prompt</c> and its cancels), which register durable Orleans
/// reminders. The reminder CALLBACK is not here: <c>ReceiveReminder</c> stays in the core part
/// alongside <c>ChatAsync</c>, because the two turn-scoped-field clears must be read together.
/// Scheduling is refused for an ephemeral chat (ADR 0034) — a durable reminder would later fire the
/// persisting, pushing reminder path.
/// </summary>
public sealed partial class AgentGrain
{
    public Task<IReadOnlyList<ReminderInfo>> GetScheduledPromptsAsync()
        => Task.FromResult<IReadOnlyList<ReminderInfo>>(
            [.. _state.State.ScheduledPrompts.Values.OrderBy(r => r.CreatedAt)]);

    /// <summary>
    /// Agent tool: schedules an Orleans reminder for this conversation. Exposed to the
    /// model as <c>schedule_prompt</c>; see <c>Skills/scheduledprompt/SKILL.md</c> for guidance.
    /// </summary>
    private async Task<string> SchedulePromptAsync(
        [Description("A short, stable identifier for the reminder, e.g. 'stand-up'. Reusing a name updates the existing reminder.")] string name,
        [Description("The prompt that is sent to the agent when the reminder fires, e.g. 'Remind the user to stand up and stretch.'")] string prompt,
        [Description("Minutes from now until the reminder first fires. Must be at least 1.")] double dueInMinutes,
        [Description("Minutes between repeats after the first firing. Must be at least 1.")] double repeatEveryMinutes)
    {
        // Scheduling is unavailable in an ephemeral chat (ADR 0034): a durable Orleans reminder would fire
        // ReceiveReminder, which persists the session and sends the away-reply Web Push (ADR 0028) — both
        // break the incognito guarantee. Refuse before registering anything, so nothing durable is created.
        if (_ephemeral)
        {
            return "Scheduling isn't available in an ephemeral (incognito) chat, because a reminder would " +
                "have to persist this conversation to fire. Turn off incognito to schedule reminders.";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return "The reminder needs a short name.";
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "The reminder needs a prompt to send to the agent when it fires.";
        }

        if (dueInMinutes < 1 || repeatEveryMinutes < 1)
        {
            return "Orleans reminders fire at most once per minute, so both the initial " +
                "delay and the repeat interval must be at least 1 minute.";
        }

        await this.RegisterOrUpdateReminder(
            name,
            TimeSpan.FromMinutes(dueInMinutes),
            TimeSpan.FromMinutes(repeatEveryMinutes));

        _state.State.ScheduledPrompts[name] = new ReminderInfo(
            name, prompt, dueInMinutes, repeatEveryMinutes, DateTimeOffset.UtcNow, TimesFired: 0);
        await _state.WriteStateAsync();

        _logger.LogInformation(
            "Agent grain {GrainKey} scheduled reminder '{Reminder}' (first in {Due} min, every {Period} min)",
            this.GetPrimaryKeyString(), name, dueInMinutes, repeatEveryMinutes);

        return $"Reminder '{name}' scheduled: first firing in {dueInMinutes} minute(s), " +
            $"repeating every {repeatEveryMinutes} minute(s).";
    }

    /// <summary>
    /// Agent tool: cancels a single scheduled reminder by name. Exposed to the model as
    /// <c>cancel_scheduled_prompt</c>; see <c>Skills/scheduledprompt/SKILL.md</c> for guidance.
    /// </summary>
    private async Task<string> CancelScheduledPromptAsync(
        [Description("The name of the reminder to cancel — the same short identifier used when it was scheduled.")] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Tell me which reminder to cancel by its name.";
        }

        if (!_state.State.ScheduledPrompts.ContainsKey(name))
        {
            return $"There is no scheduled reminder named '{name}' in this conversation.";
        }

        // The durable reminder keeps ticking unless it is explicitly unregistered —
        // clearing the state entry alone would leave it firing (see PurgeAsync).
        var reminder = await this.GetReminder(name);
        if (reminder is not null)
        {
            await this.UnregisterReminder(reminder);
        }

        _state.State.ScheduledPrompts.Remove(name);
        await _state.WriteStateAsync();

        _logger.LogInformation(
            "Agent grain {GrainKey} cancelled reminder '{Reminder}'",
            this.GetPrimaryKeyString(), name);

        return $"Reminder '{name}' cancelled.";
    }

    /// <summary>
    /// Agent tool: cancels every scheduled reminder for this conversation. Exposed to the
    /// model as <c>cancel_all_scheduled_prompts</c>; see <c>Skills/scheduledprompt/SKILL.md</c>.
    /// </summary>
    private async Task<string> CancelAllScheduledPromptsAsync()
    {
        if (_state.State.ScheduledPrompts.Count == 0)
        {
            return "There are no scheduled reminders to cancel in this conversation.";
        }

        var count = _state.State.ScheduledPrompts.Count;

        // Durable reminders must each be unregistered explicitly, or they keep firing
        // after the state is cleared (same reason PurgeAsync iterates them).
        foreach (var reminder in await this.GetReminders())
        {
            await this.UnregisterReminder(reminder);
        }

        _state.State.ScheduledPrompts.Clear();
        await _state.WriteStateAsync();

        _logger.LogInformation(
            "Agent grain {GrainKey} cancelled all {Count} scheduled reminder(s)",
            this.GetPrimaryKeyString(), count);

        return $"Cancelled {count} scheduled reminder(s).";
    }
}
