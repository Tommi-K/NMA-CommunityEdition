using System.Diagnostics;
using System.Runtime.InteropServices;
using CliWrap;
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
    /// and that the exit status is left to whoever was going to collect it.
    /// </remarks>
    private static readonly TimeSpan ChildCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Grace for the output pipes to drain once the child is known to have gone.
    /// </summary>
    private static readonly TimeSpan OutputSettleDelay = TimeSpan.FromSeconds(1);

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
        var startTime = DateTimeOffset.UtcNow;
        var task = command.ExecuteAsync(cancellationToken: cancellationToken);

        // Windows doesn't report child exits through signals, so there is nothing to back up.
        if (_fileSystem.OS.IsWindows) return await task;

        while (true)
        {
            var finished = await Task.WhenAny(task.Task, Task.Delay(ChildCheckInterval, cancellationToken));
            if (ReferenceEquals(finished, task.Task)) return await task;

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
