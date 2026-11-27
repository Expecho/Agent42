using System.Text;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// Proactive chat delivery (ADR 0111, C9): the conversation's chat origin, and the hand-off of an away reply or a
/// model notification to <see cref="ChatDeliveryRelay"/>.
///
/// <para><b>Reply text is accumulated only where an away reply is raised</b>, never in <c>ChatCoreAsync</c>:
/// <c>EnqueuePromptAsync</c> drains its events through <see cref="AwayReplyText"/>, and the reminder reads
/// <c>LastAssistantText</c>. A chat-driven turn is streamed by the gateway's pipeline through <c>ChatAsync</c>, so it
/// never reaches either site and is never delivered back to the chat it came from. Like
/// <c>SendAwayReplyPushAsync</c> (ADR 0028), do not call this from <c>ChatCoreAsync</c>.</para>
/// </summary>
public sealed partial class AgentGrain
{
    public async Task SetChatOriginAsync(ChatOrigin origin)
    {
        // Idempotent and write-once: a conversation keeps the chat that minted it, so a retried first turn or a
        // later caller can never move it.
        if (_state.State.Origin is not null)
        {
            return;
        }

        _state.State.Origin = new ChatOrigin { ConnectionId = origin.ConnectionId, RouteKey = origin.RouteKey, IsGroup = origin.IsGroup };
        await _state.WriteStateAsync();
    }

    /// <summary>
    /// The reply text of a drained run: <c>TextDelta</c> appends, <c>TextReset</c> (a research iteration, ADR 0082)
    /// clears. Bounded at <see cref="WebhookLimits.MaxReplyChars"/> so a runaway reply cannot grow the activation; the
    /// text past the bound is dropped, and the sender receives what fits.
    /// </summary>
    private sealed class AwayReplyText
    {
        private readonly StringBuilder _text = new();

        public string Text => _text.ToString();

        public void Observe(AgentStreamEvent evt)
        {
            switch (evt.Type)
            {
                case AgentStreamEventType.TextReset:
                    _text.Clear();
                    break;
                case AgentStreamEventType.TextDelta when evt.Text is { Length: > 0 } delta:
                    var room = WebhookLimits.MaxReplyChars - _text.Length;
                    if (room > 0)
                    {
                        _text.Append(delta.Length <= room ? delta : delta[..room]);
                    }

                    break;
            }
        }
    }
}
