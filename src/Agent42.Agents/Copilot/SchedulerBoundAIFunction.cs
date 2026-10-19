using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// Runs a tool on a captured <see cref="TaskScheduler"/>: the grain activation's own scheduler, captured while
/// <c>BuildAgent</c> runs.
///
/// <para><b>Why:</b> the Copilot runtime invokes a custom tool from the SDK's RPC thread, not from the turn that is
/// awaiting the stream. Measured: a turn started on an exclusive scheduler saw its tool run on
/// <c>thread=12 onExclusive=False syncCtx=null</c>. Agent42's tools are grain methods. They read and write
/// <c>_state</c>, register reminders and call other grains, and none of that is safe off the activation. A
/// <c>FunctionInvokingChatClient</c> calls them from the turn itself, so an ordinary chat never needed this.</para>
///
/// <para>Scheduling onto the activation while the turn is suspended (awaiting the stream that is waiting for this
/// very tool) is the documented Orleans pattern for getting back into a grain's context, and it interleaves exactly
/// like a normal tool callback.</para>
///
/// <para><b>The run's <see cref="TurnContext"/> (ADR 0106) is re-established here.</b> Nothing flows from the turn to
/// the RPC thread, so a tool would read <see cref="TurnContext.Closed"/>. The registry of Copilot runs in flight
/// (<paramref name="runsInFlight"/>, read on the activation's scheduler) is authoritative: a context that did flow is
/// kept only while its run is still registered, because an SDK that captured one once (a lazily started RPC loop)
/// would otherwise hand every later call a finished run's budget. Failing that, the one run in flight is used; with
/// none or several the tool runs under <see cref="TurnContext.Closed"/> and refuses.</para>
/// </summary>
public sealed class SchedulerBoundAIFunction(
    AIFunction inner, TaskScheduler scheduler, Func<IReadOnlyList<TurnContext>> runsInFlight)
    : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var flowed = TurnContext.CurrentOrClosed;
        if (TaskScheduler.Current == scheduler)
        {
            return await InvokeInRunAsync(flowed, arguments, cancellationToken);
        }

        return await Task.Factory.StartNew(
            () => InvokeInRunAsync(flowed, arguments, cancellationToken).AsTask(),
            cancellationToken,
            TaskCreationOptions.None,
            scheduler).Unwrap();
    }

    // On the activation's scheduler, so the registry lookup reads grain state where it is safe to.
    private ValueTask<object?> InvokeInRunAsync(
        TurnContext flowed, AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var context = Resolve(flowed, runsInFlight());
        return TurnContext.RunInAsync(context, () => base.InvokeCoreAsync(arguments, cancellationToken));
    }

    private static TurnContext Resolve(TurnContext flowed, IReadOnlyList<TurnContext> runs)
    {
        if (runs.Contains(flowed))
        {
            return flowed;
        }

        return runs.Count == 1 ? runs[0] : TurnContext.Closed;
    }
}
