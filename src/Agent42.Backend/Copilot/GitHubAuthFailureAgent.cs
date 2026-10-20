using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// Wraps a copilot agent that signs in with the user's GitHub account (ADR 0108). When GitHub rejects the token (it was
/// revoked, or the account lost its Copilot plan) before the run produced anything, the run answers with
/// <see cref="Reconnect"/> instead of failing: a failed stream reaches the browser only as "Transmission failed", with
/// nothing the user can act on. Any other failure, and any failure after output started, propagates unchanged.
/// </summary>
public sealed class GitHubAuthFailureAgent(AIAgent inner) : DelegatingAIAgent(inner)
{
    public const string Reconnect =
        "_GitHub rejected this copilot agent's sign-in, so your Copilot subscription can't be used right now. Sign in "
        + "with GitHub again in Settings → Providers._";

    /// <summary>
    /// Whether an exception (or one it wraps) is the runtime reporting a rejected or missing GitHub credential. The
    /// texts are measured (SDK 1.0.16): a bad token fails with <c>401 Unauthorized: {"message":"Bad credentials"}</c>,
    /// a missing one with <c>No GitHub OAuth token or Copilot HMAC key provided</c>.
    /// </summary>
    public static bool IsGitHubAuthFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var m = e.Message;
            if ((m.Contains("401", StringComparison.Ordinal) && m.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
                || m.Contains("Bad credentials", StringComparison.OrdinalIgnoreCase)
                || m.Contains("No GitHub OAuth token", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Not authenticated", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.RunCoreAsync(messages, session, options, cancellationToken);
        }
        catch (Exception ex) when (IsGitHubAuthFailure(ex))
        {
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, Reconnect));
        }
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var updates = base.RunCoreStreamingAsync(messages, session, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        var started = false;
        while (await NextAsync(updates, started) is { } update)
        {
            started = true;
            yield return update;
            if (ReferenceEquals(update.RawRepresentation, Reconnect))
            {
                yield break;
            }
        }
    }

    // The next update, null at the end, or the Reconnect answer (marked by its RawRepresentation) when GitHub rejected
    // the credential before anything was produced. A yield cannot sit in a catch, hence the helper.
    private static async Task<AgentResponseUpdate?> NextAsync(IAsyncEnumerator<AgentResponseUpdate> updates, bool started)
    {
        try
        {
            return await updates.MoveNextAsync() ? updates.Current : null;
        }
        catch (Exception ex) when (!started && IsGitHubAuthFailure(ex))
        {
            return new AgentResponseUpdate(ChatRole.Assistant, Reconnect) { RawRepresentation = Reconnect };
        }
    }
}
