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
/// </summary>
public sealed class SchedulerBoundAIFunction(AIFunction inner, TaskScheduler scheduler) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (TaskScheduler.Current == scheduler)
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken);
        }

        return await Task.Factory.StartNew(
            () => base.InvokeCoreAsync(arguments, cancellationToken).AsTask(),
            cancellationToken,
            TaskCreationOptions.None,
            scheduler).Unwrap();
    }
}
