namespace Agent42.LocalBridge;

/// <summary>
/// Entry point of the local bridge helper (ADR 0076). A class in a namespace rather than top-level
/// statements: the test project references this assembly <i>and</i> the backend, and two global
/// <c>Program</c> types would collide there.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var (options, error) = BridgeOptions.Parse(args, Environment.GetEnvironmentVariable);
        if (options is null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(BridgeOptions.Usage);
            return 2;
        }

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var gate = new ConfirmationGate(new ConsolePrompt(), options.AutoApprove);
        var client = new BridgeClient(options, new HttpUpstream(options.Upstream), gate, Log);
        return await client.RunAsync(stop.Token) switch
        {
            BridgeExit.Unauthorized => 1,
            BridgeExit.Replaced => 3,
            _ => 0,
        };
    }

    // Never the key, never a tool call's arguments — those are shown only inside a confirmation prompt.
    private static void Log(string message) => Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");
}

/// <summary>
/// The console confirmation (ADR 0076). The tool name stands alone on its own line, first, so no argument
/// text can visually replace it; both inputs arrive already sanitized by <see cref="ConfirmationGate"/>.
/// Only an explicit <c>y</c> allows; anything else — including no answer before the timeout — denies.
/// </summary>
public sealed class ConsolePrompt : IConfirmationPrompt
{
    // Console.ReadLine cannot be cancelled, so ONE background reader owns stdin and each prompt drains what
    // was typed before it asked. A per-prompt ReadLine abandoned on timeout would instead swallow the answer
    // meant for the next prompt — and a stale "y" must never approve a call the user has not seen.
    private readonly System.Threading.Channels.Channel<string> _lines =
        System.Threading.Channels.Channel.CreateUnbounded<string>();

    public ConsolePrompt()
    {
        var reader = new Thread(() =>
        {
            while (Console.ReadLine() is { } line)
            {
                _lines.Writer.TryWrite(line);
            }

            _lines.Writer.TryComplete();
        })
        { IsBackground = true, Name = "console-prompt" };
        reader.Start();
    }

    public async Task<bool> ConfirmAsync(string toolName, string arguments, CancellationToken cancellationToken)
    {
        while (_lines.Reader.TryRead(out _))
        {
            // discard anything typed before this prompt was shown
        }

        Console.WriteLine();
        Console.WriteLine("──── Agent42 wants to run a browser action ────");
        Console.WriteLine($"Tool: {toolName}");
        Console.WriteLine("Arguments:");
        Console.WriteLine(arguments);
        Console.Write("Allow? [y/N] ");

        try
        {
            var line = await _lines.Reader.ReadAsync(cancellationToken);
            return string.Equals(line.Trim(), "y", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            return false; // stdin closed: nobody can answer
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("(no answer — declined)");
            throw;
        }
    }
}
