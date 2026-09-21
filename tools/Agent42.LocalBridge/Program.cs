using System.Reflection;

namespace Agent42.LocalBridge;

/// <summary>
/// Entry point of the local bridge helper (ADR 0076). A class in a namespace rather than top-level
/// statements: the test project references this assembly <i>and</i> the backend, and two global
/// <c>Program</c> types would collide there.
/// </summary>
public static class Program
{
    /// <summary>The release version, stamped from the tag by the release workflow; <c>0.0.0-dev</c> locally.</summary>
    public static string Version =>
        typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "dev";

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--version"])
        {
            Console.WriteLine(Version);
            return 0;
        }

        Log($"Agent42 local bridge {Version}");
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

        if (options.Yolo)
        {
            Log("--yolo: EVERY tool call runs without asking, including ones that drive your signed-in browser.");
        }

        var gate = new ConfirmationGate(new ConsolePrompt(), options.AutoApprove, allowAll: options.Yolo);
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
/// Only an explicit <c>y</c> or <c>a</c> allows; anything else — including no answer before the timeout —
/// denies. <c>a</c> also stops this tool asking again for as long as the helper runs.
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

    public async Task<ConfirmationAnswer> ConfirmAsync(string toolName, string arguments, CancellationToken cancellationToken)
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
        Console.Write("Allow? [y]es once / [n]o / [a]lways for this tool: ");

        try
        {
            var answer = Read(await _lines.Reader.ReadAsync(cancellationToken));
            if (answer == ConfirmationAnswer.AllowForSession)
            {
                Console.WriteLine($"({toolName} will now run without asking until this bridge stops)");
            }

            return answer;
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            return ConfirmationAnswer.Deny; // stdin closed: nobody can answer
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("(no answer — declined)");
            throw;
        }
    }

    /// <summary>
    /// Maps one typed line to an answer. Only the exact words below allow anything; every other line — a
    /// typo, a stray paste, an empty return — denies, because this is the gate in front of the user's
    /// signed-in browser and a near-miss must never read as consent.
    /// </summary>
    public static ConfirmationAnswer Read(string line) => line.Trim().ToLowerInvariant() switch
    {
        "a" or "all" or "always" => ConfirmationAnswer.AllowForSession,
        "y" or "yes" => ConfirmationAnswer.Allow,
        _ => ConfirmationAnswer.Deny,
    };
}
