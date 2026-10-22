using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Agent42.Backend;

/// <summary>What to run: an executable, its arguments (no shell), extra environment, and where its output lines go.</summary>
public sealed record SignalProcessStartInfo(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    Action<string> OnOutputLine);

/// <summary>A started child process, as <see cref="SignalDaemon"/> drives it.</summary>
public interface ISignalProcess : IDisposable
{
    /// <summary>Completes when the process has exited (for any reason).</summary>
    Task Exited { get; }

    /// <summary>Whether the process has already exited.</summary>
    bool HasExited { get; }

    /// <summary>Asks the process to shut down (SIGTERM), so SQLite closes cleanly.</summary>
    void Terminate();

    /// <summary>Ends the process at once (SIGKILL).</summary>
    void Kill();
}

/// <summary>The seam <see cref="SignalDaemon"/> starts signal-cli through, so tests never spawn a JVM.</summary>
public interface ISignalProcessLauncher
{
    /// <summary>Starts the process. Output lines (stdout and stderr) are passed to <see cref="SignalProcessStartInfo.OnOutputLine"/>.</summary>
    ISignalProcess Start(SignalProcessStartInfo info);
}

/// <summary>
/// The production launcher: <see cref="Process"/> with no shell and an argument list. Deliberately thin, and not
/// unit-tested (the accepted coverage gap): everything behavioural lives in <see cref="SignalDaemon"/>.
/// </summary>
/// <summary>The only variables the signal-cli process inherits from the backend's environment.</summary>
public static class SignalProcessEnvironment
{
    public static readonly IReadOnlyList<string> Inherited = ["PATH", "JAVA_HOME", "HOME", "LANG", "TZ", "TMPDIR"];
}

public sealed class SignalProcessLauncher : ISignalProcessLauncher
{
    public ISignalProcess Start(SignalProcessStartInfo info)
    {
        var startInfo = new ProcessStartInfo(info.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        foreach (var argument in info.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // A scrubbed environment: the JVM never inherits the backend's (connection strings, API keys, the managed
        // identity's IDENTITY_ENDPOINT/HEADER). It gets only what it needs to run, plus what the caller passes.
        var inherited = startInfo.Environment.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        startInfo.Environment.Clear();
        foreach (var name in SignalProcessEnvironment.Inherited)
        {
            if (inherited.TryGetValue(name, out var value) && value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }

        foreach (var (key, value) in info.Environment)
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        process.OutputDataReceived += (_, e) => Forward(e.Data, info.OnOutputLine);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, info.OnOutputLine);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new ChildProcess(process, exited.Task);
    }

    private static void Forward(string? line, Action<string> sink)
    {
        if (!string.IsNullOrEmpty(line))
        {
            sink(line);
        }
    }

    private sealed class ChildProcess(Process process, Task exited) : ISignalProcess
    {
        private const int SigTerm = 15;

        public Task Exited { get; } = exited;

        public bool HasExited => Exited.IsCompleted;

        // .NET's Process.Kill() is SIGKILL and there is no managed SIGTERM, so Linux/macOS call libc's kill(2) (the
        // simplest robust way; production is Linux). signal-cli's launcher script `exec`s the JVM, so the pid is the
        // JVM itself and the signal reaches its shutdown hook. Elsewhere (a Windows dev box) fall back to Kill.
        public void Terminate()
        {
            if (HasExited)
            {
                return;
            }

            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                Kill();
                return;
            }

            try
            {
                _ = SendSignal(process.Id, SigTerm);
            }
            catch (InvalidOperationException)
            {
                // Exited between the check and the call.
            }
        }

        public void Kill()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        public void Dispose() => process.Dispose();

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int SendSignal(int pid, int signal);
    }
}
