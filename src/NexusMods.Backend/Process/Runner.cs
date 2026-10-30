using System.Diagnostics;
using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.CrossPlatform;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;

namespace NexusMods.Backend.Process;

internal class ProcessRunner : IProcessRunner
{
    private readonly ILogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly AbsolutePath _processLogsFolder;

    public ProcessRunner(IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<ProcessRunner>>();
        _fileSystem = serviceProvider.GetRequiredService<IFileSystem>();

        // TODO: rework
        var settingsManager = serviceProvider.GetRequiredService<ISettingsManager>();
        _processLogsFolder = LoggingSettings.GetLogBaseFolder(_fileSystem.OS, _fileSystem).Combine("ProcessLogs");
        _logger.LogInformation("Using process log folder at {Path}", _processLogsFolder);

        _processLogsFolder.CreateDirectory();
    }

    private string GetFileName(Command command)
    {
        return PathHelpers.IsRooted(command.TargetFilePath)
            ? _fileSystem.FromUnsanitizedFullPath(command.TargetFilePath).FileName
            : RelativePath.FromUnsanitizedInput(command.TargetFilePath).FileName.ToString();
    }

    private static string GetLogFileName(string fileName)
    {
        // TODO: consider smaller IDs for shorted file names
        var id = Guid.NewGuid();
        return $"{fileName}-{id:D}";
    }

    public void Run(Command command, bool logOutput)
    {
        var task = ExecuteCommand(command, logOutput, cancellationToken: CancellationToken.None);
        task.FireAndForget(_logger, cancellationToken: CancellationToken.None);
    }

    public Task<CommandResult> RunAsync(Command command, bool logOutput, CancellationToken cancellationToken = default)
    {
        command = SetupLogging(command, logOutput);
        return ExecuteCommand(command, logOutput, cancellationToken);
    }

    private async Task<CommandResult> ExecuteCommand(Command command, bool logOutput, CancellationToken cancellationToken)
    {
        if (logOutput) _logger.LogDebug("Starting command `{Command}`", command.ToString());

        var sw = Stopwatch.StartNew();
        var result = await Execute(command, cancellationToken);
        sw.Stop();

        if (!logOutput) return result;
        _logger.LogDebug("Command `{Command}` finished after {RunTime} seconds with exit Code {ExitCode}", command.ToString(), result.RunTime.TotalSeconds, result.ExitCode);
        return result;
    }

    /// <summary>
    /// How long to leave the normal wait alone before asking the OS about the child directly.
    /// </summary>
    /// <remarks>
    /// Long enough that a command which simply takes a while is never asked about twice over,
    /// and that the exit status is left to whoever was going to collect it. On Linux the child
    /// is only looked at before then, never collected; see <see cref="ExecuteOnLinux"/>.
    /// </remarks>
    private static readonly TimeSpan ChildCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Grace for the output pipes to drain once the child is known to have gone.
    /// </summary>
    private static readonly TimeSpan OutputSettleDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The first gap between looks at a running child on Linux, doubled after every look.
    /// </summary>
    private static readonly TimeSpan FirstExitCheck = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// The gap that <see cref="FirstExitCheck"/> grows to and then stays at.
    /// </summary>
    /// <remarks>
    /// A look is a single system call, so even a child that runs for minutes costs nothing
    /// worth counting, and one that exits is noticed within this long.
    /// </remarks>
    private static readonly TimeSpan LongestExitCheck = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long the normal wait gets to complete once the output has been read, before the
    /// status the OS reported is used instead.
    /// </summary>
    /// <remarks>
    /// Where SIGCHLD still reaches this process it is almost always enough, so the command
    /// completes the usual way. Once an exit has gone unreported it isn't waited for at all:
    /// Chromium doesn't hand SIGCHLD back.
    /// </remarks>
    private static readonly TimeSpan NormalWaitGrace = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Set once a child's exit has gone unreported: from then on the normal wait isn't given
    /// its grace, and only the first one is worth more than a debug line.
    /// </summary>
    private int _exitsGoUnreported;

    /// <summary>
    /// Runs the command, with a backstop for a child process whose exit this process is never
    /// told about.
    /// </summary>
    /// <remarks>
    /// Waiting for a child to exit relies on SIGCHLD, and a process gets one handler for it.
    /// Chromium installs its own when the in-app browser starts, and from then on nothing here
    /// hears that a child has exited: <c>7zz</c> lists an archive, prints all of it, exits --
    /// and the wait below never returns, so adding a collection to the library stops dead with
    /// nothing logged and no error shown. The child is left unreaped, which is also what tells
    /// it apart from one that is still working.
    ///
    /// Hence the poll: when the OS says the child has gone, its exit status is collected here
    /// and the command carries on. Everywhere the usual path still works -- the CLI, and the UI
    /// until Chromium is running -- the command completes first and the poll never fires.
    /// </remarks>
    private async Task<CommandResult> Execute(Command command, CancellationToken cancellationToken)
    {
        // Windows doesn't report child exits through signals, so there is nothing to back up.
        if (_fileSystem.OS.IsWindows) return await command.ExecuteAsync(cancellationToken: cancellationToken);

        // NOTE(CE): the layout PeekAtChild reads is that of 64-bit Linux
        if (OperatingSystem.IsLinux() && Environment.Is64BitProcess) return await ExecuteOnLinux(command, cancellationToken);

        var startTime = DateTimeOffset.UtcNow;
        var task = command.ExecuteAsync(cancellationToken: cancellationToken);

        while (true)
        {
            var finished = await Task.WhenAny(task.Task, Task.Delay(ChildCheckInterval, cancellationToken));
            if (ReferenceEquals(finished, task.Task)) return await task;

            // A cancelled delay finishes at once, so without this the loop would spin until the
            // killed child is gone and then report the kill as a command that failed.
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryCollectExitedChild(task.ProcessId, out var exitCode)) continue;

            // The child has gone, so its output is at an end; give the pipes their moment to
            // finish, which is usually all that was missing.
            var settled = await Task.WhenAny(task.Task, Task.Delay(OutputSettleDelay, cancellationToken));
            if (ReferenceEquals(settled, task.Task)) return await task;

            _logger.LogWarning("Command `{Command}` (pid {ProcessId}) exited with code {ExitCode} without this process being told; taking its exit status directly", command.ToString(), task.ProcessId, exitCode);

            if (exitCode != 0 && command.Validation.HasFlag(CommandResultValidation.ZeroExitCode))
                throw new Exception($"Command `{command}` exited with code {exitCode}");

            return new CommandResult(exitCode, startTime, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// <see cref="Execute"/> on Linux, which can say whether a child has exited without that
    /// also collecting it.
    /// </summary>
    /// <remarks>
    /// Waiting out <see cref="ChildCheckInterval"/> before every look cost each command at least
    /// six seconds once the in-app browser was up, twice per 7z or RAR archive added to the
    /// library. Looking sooner was never the problem, collecting was: it takes the exit status
    /// away from everyone else, and .NET finding one of its own children already collected is
    /// fatal to the process. So the child is only looked at here (<c>WNOWAIT</c>), often enough
    /// to notice its exit within milliseconds, and the command completes on the status the OS
    /// reports once the output has been read to its end. Collecting the child is left to whoever
    /// normally does it, with a backstop for when that is nobody; see <see cref="CollectLater"/>.
    /// </remarks>
    private async Task<CommandResult> ExecuteOnLinux(Command command, CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        var outputRead = WatchForEnd(command.StandardOutputPipe, out var standardOutput);
        var errorRead = WatchForEnd(command.StandardErrorPipe, out var standardError);
        command = command.WithStandardOutputPipe(standardOutput).WithStandardErrorPipe(standardError);

        // CliWrap gets a token of its own rather than the caller's: once the command has
        // completed on the status taken here, CliWrap is still waiting on the child, and a
        // cancellation reaching it after that would kill whatever process has the id by then.
        using var killChild = new CancellationTokenSource();
        using var forwardCancellation = cancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), killChild);

        var task = command.ExecuteAsync(killChild.Token);

        try
        {
            return await WaitForChild(command, task, startTime, outputRead, errorRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Killed along with the cancellation, and no more likely to be collected than any
            // other child.
            CollectLater(task.ProcessId);
            throw;
        }
    }

    /// <summary>
    /// The waiting half of <see cref="ExecuteOnLinux"/>.
    /// </summary>
    private async Task<CommandResult> WaitForChild(
        Command command,
        CommandTask<CommandResult> task,
        DateTimeOffset startTime,
        Task outputRead,
        Task errorRead,
        CancellationToken cancellationToken)
    {
        var checkInterval = FirstExitCheck;

        while (true)
        {
            var finished = await Task.WhenAny(task.Task, Task.Delay(checkInterval, cancellationToken));
            if (ReferenceEquals(finished, task.Task)) return await task;

            // A cancelled delay finishes at once, so without this the loop would spin until the
            // killed child is gone and then report the kill as a command that failed.
            cancellationToken.ThrowIfCancellationRequested();

            var child = PeekAtChild(task.ProcessId, out var exitCode);
            if (child is ChildState.Running)
            {
                checkInterval = checkInterval * 2 < LongestExitCheck ? checkInterval * 2 : LongestExitCheck;
                continue;
            }

            // The child has gone, so its output is at an end: once that has been read there is
            // nothing left to wait for. Bounded, since a child it started can hold the pipes open.
            await Task.WhenAny(task.Task, Task.WhenAll(outputRead, errorRead), Task.Delay(OutputSettleDelay, cancellationToken));
            if (!task.Task.IsCompleted && Volatile.Read(ref _exitsGoUnreported) == 0)
                await Task.WhenAny(task.Task, Task.Delay(NormalWaitGrace, cancellationToken));

            if (task.Task.IsCompleted) return await task;

            cancellationToken.ThrowIfCancellationRequested();

            if (child is ChildState.Collected)
            {
                // Collected by someone else before it could be looked at, and its status with it.
                // In the CLI that someone is .NET, and the normal wait is about to complete.
                await Task.WhenAny(task.Task, Task.Delay(ChildCheckInterval, cancellationToken));
                if (task.Task.IsCompleted) return await task;

                cancellationToken.ThrowIfCancellationRequested();

                // The alternative is waiting on a child that nothing will ever report on.
                exitCode = 0;
            }
            else
            {
                CollectLater(task.ProcessId);
            }

            if (Interlocked.Exchange(ref _exitsGoUnreported, 1) == 0)
            {
                _logger.LogInformation("Command `{Command}` (pid {ProcessId}) exited with code {ExitCode} without this process being told, as children do once the in-app browser has taken over SIGCHLD; exit statuses are taken from the OS directly from here on", command.ToString(), task.ProcessId, exitCode);
            }
            else
            {
                _logger.LogDebug("Command `{Command}` (pid {ProcessId}) exited with code {ExitCode}, taken from the OS directly", command.ToString(), task.ProcessId, exitCode);
            }

            if (exitCode != 0 && command.Validation.HasFlag(CommandResultValidation.ZeroExitCode))
                throw new CommandExecutionException(command, exitCode, $"Command `{command}` exited with code {exitCode}");

            return new CommandResult(exitCode, startTime, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Collects an exited child once it is clear that nothing else will, so it doesn't sit in
    /// the process table for as long as the app runs.
    /// </summary>
    /// <remarks>
    /// Whatever collects children here does so the moment one exits, so a child still waiting
    /// a whole <see cref="ChildCheckInterval"/> after its exit is one nobody is going to.
    /// </remarks>
    private void CollectLater(int processId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ChildCheckInterval);
                if (PeekAtChild(processId, out _) is ChildState.Exited) TryCollectExitedChild(processId, out _);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Failed to collect exited child {ProcessId}", processId);
            }
        });
    }

    /// <summary>
    /// Wraps <paramref name="target"/> in <paramref name="watched"/>, which completes the
    /// returned task once it has read the stream to its end.
    /// </summary>
    private static Task WatchForEnd(PipeTarget target, out PipeTarget watched)
    {
        // CliWrap doesn't open the stream of a null target at all, so there is no end to see.
        // Wrapping one would open it, and then something would have to read it.
        if (target == PipeTarget.Null)
        {
            watched = target;
            return Task.CompletedTask;
        }

        var end = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watched = new EndSignallingPipeTarget(target, end);
        return end.Task;
    }

    private sealed class EndSignallingPipeTarget(PipeTarget inner, TaskCompletionSource end) : PipeTarget
    {
        public override async Task CopyFromAsync(Stream origin, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.CopyFromAsync(origin, cancellationToken);
            }
            finally
            {
                end.TrySetResult();
            }
        }
    }

    private enum ChildState
    {
        Running,
        Exited,

        /// <summary>
        /// No longer there to be waited for: somebody else collected it.
        /// </summary>
        Collected,
    }

    /// <summary>
    /// The start of <c>siginfo_t</c> as <c>waitid</c> fills it in for a child, on 64-bit Linux.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct ChildSignalInfo
    {
        [FieldOffset(8)] public int Code;
        [FieldOffset(16)] public int ProcessId;
        [FieldOffset(24)] public int Status;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int waitid(int idType, int id, ref ChildSignalInfo info, int options);

    private const int P_PID = 1;
    private const int WEXITED = 4;
    private const int WNOWAIT = 0x01000000;
    private const int CLD_EXITED = 1;

    /// <summary>
    /// Asks the OS whether the child has exited, leaving it to be collected just as if nobody
    /// had asked.
    /// </summary>
    /// <param name="processId">The child.</param>
    /// <param name="exitCode">The code it exited with, or 128 plus the signal that ended it.</param>
    private static ChildState PeekAtChild(int processId, out int exitCode)
    {
        exitCode = 0;

        int result;
        ChildSignalInfo info;
        do
        {
            // Zeroed before every call: an unchanged child is told apart by the id left at zero.
            info = default;
            result = waitid(P_PID, processId, ref info, WEXITED | WNOHANG | WNOWAIT);
        } while (result == -1 && Marshal.GetLastWin32Error() == EINTR);

        if (result == -1) return ChildState.Collected;
        if (info.ProcessId != processId) return ChildState.Running;

        exitCode = info.Code == CLD_EXITED ? info.Status : 128 + info.Status;
        return ChildState.Exited;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    private const int WNOHANG = 1;
    private const int EINTR = 4;
    private const int ECHILD = 10;

    /// <summary>
    /// Collects the child's exit status if it has one to give, without waiting for it.
    /// </summary>
    /// <returns>False while the child is still running, or while its status is not ours to read.</returns>
    private static bool TryCollectExitedChild(int processId, out int exitCode)
    {
        exitCode = 0;

        int result;
        do
        {
            result = waitpid(processId, out var status, WNOHANG);
            if (result == processId)
            {
                // The low 7 bits hold the signal that ended it, if a signal did; otherwise the
                // next 8 are the code it exited with.
                var terminatingSignal = status & 0x7f;
                exitCode = terminatingSignal == 0 ? (status >> 8) & 0xff : 128 + terminatingSignal;
                return true;
            }
        } while (result == -1 && Marshal.GetLastWin32Error() == EINTR);

        if (result == 0) return false;

        // Somebody else collected the status first, so the code it exited with is gone with it.
        // Only act on that once the process itself is really gone, and then assume it was fine:
        // the alternative is waiting on a child that nothing will ever report on.
        return Marshal.GetLastWin32Error() == ECHILD && !Directory.Exists($"/proc/{processId}");
    }

    private Command SetupLogging(Command command, bool logOutput)
    {
        var stdInPipe = command.StandardInputPipe == PipeSource.Null ? PipeSource.Null : command.StandardInputPipe;

        if (!logOutput)
        {
            // We require a non-null pipe here, for more details, see:
            // https://github.com/Nexus-Mods/NexusMods.App/issues/1905#issuecomment-2302503110
            // https://github.com/Nexus-Mods/NexusMods.App/issues/1905#issuecomment-2302486535
            command = command.WithStandardInputPipe(stdInPipe);
            if (command.StandardOutputPipe == PipeTarget.Null) command = command.WithStandardOutputPipe(PipeTarget.ToStream(Stream.Null));
            if (command.StandardOutputPipe == PipeTarget.Null) command = command.WithStandardErrorPipe(PipeTarget.ToStream(Stream.Null));
            return command;
        }

        var fileName = GetFileName(command);
        var logFileName = GetLogFileName(fileName);
        var stdOutFilePath = _processLogsFolder.Combine(logFileName + ".stdout.log");
        var stdErrFilePath = _processLogsFolder.Combine(logFileName + ".stderr.log");

        var stdOutPipe = PipeTarget.Create(async (stdOut, cancellationToken) =>
        {
            await using var fileStream = stdOutFilePath.Open(FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            await stdOut.CopyToAsync(fileStream, cancellationToken: cancellationToken);
        });

        var stdErrPipe = PipeTarget.Create(async (stdOut, cancellationToken) =>
        {
            await using var fileStream = stdErrFilePath.Open(FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            await stdOut.CopyToAsync(fileStream, cancellationToken: cancellationToken);
        });

        var mergedStdOutPipe = command.StandardOutputPipe == PipeTarget.Null ? stdOutPipe : PipeTarget.Merge(command.StandardOutputPipe, stdOutPipe);
        var mergedStdErrPipe = command.StandardErrorPipe == PipeTarget.Null ? stdErrPipe : PipeTarget.Merge(command.StandardErrorPipe, stdErrPipe);

        _logger.LogInformation("Setup process logs {StdOutLogPath} and {StdErrLogPath}", stdOutFilePath, stdErrFilePath);
        return command
            .WithStandardInputPipe(stdInPipe)
            .WithStandardOutputPipe(mergedStdOutPipe)
            .WithStandardErrorPipe(mergedStdErrPipe);
    }

    public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource();

        process.EnableRaisingEvents = true;
        var hasExited = false;

        process.Exited += (_, _) =>
        {
            hasExited = true;
            tcs.SetResult();
            process.Dispose();
        };
        
        cancellationToken.Register(() =>
        {
            if (hasExited) return;
            try
            {
                _logger.LogInformation("Killing process `{Process}`", process.StartInfo.FileName);
                process.Kill();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to kill process `{Process}`", process.StartInfo.FileName);
                tcs.SetException(e);
            }
        });

        try
        {
            _logger.LogInformation("Executing process `{Process}`", process.StartInfo.FileName);
            process.Start();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to start process `{Process}`", process.StartInfo.FileName);
            tcs.SetException(e);
        }

        return tcs.Task; 
    }
}
