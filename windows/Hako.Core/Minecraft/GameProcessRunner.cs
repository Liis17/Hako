using System.ComponentModel;
using System.Text.Json;
using Hako.Core.Instances;
using Microsoft.Win32.SafeHandles;

namespace Hako.Core.Minecraft;

public sealed record GameProcessRecord(
    Guid InstanceID,
    int Pid,
    ulong StartSeconds,
    ulong StartMicroseconds,
    Guid? SessionID = null);

public sealed record GameProcessLaunch(GameProcessRecord Record, string LogPath, Task<int?> Completion);

public sealed class GameProcessRunner
{
    private readonly Func<SafeProcessHandle, (bool Success, ulong StartSeconds, ulong StartMicroseconds)> _creationTimeProvider;
    private readonly uint? _codePage;

    public GameProcessRunner() : this(null) { }

    internal GameProcessRunner(
        Func<SafeProcessHandle, (bool Success, ulong StartSeconds, ulong StartMicroseconds)>? creationTimeProvider,
        uint? codePage = null)
    {
        _creationTimeProvider = creationTimeProvider ?? ReadCreationTime;
        _codePage = codePage;
    }

    /// <summary>Starts Minecraft without a shell and leaves it alive if Hako exits.</summary>
    public Task<GameProcessLaunch> StartAsync(
        MinecraftLaunchPlan plan,
        Guid instanceId,
        string instanceRoot,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default,
        Action<int?>? onExit = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        var codePage = _codePage ?? JavaAnsiEncoding.CurrentCodePage;
        return Task.Run(() => Start(plan, instanceId, instanceRoot, sessionId, codePage, cancellationToken, onExit), cancellationToken);
    }

    public Task<GameProcessLaunch?> RestoreAsync(
        Guid instanceId,
        string instanceRoot,
        CancellationToken cancellationToken = default,
        Action<int?>? onExit = null) => Task.Run(() => Restore(instanceId, instanceRoot, _creationTimeProvider, cancellationToken, onExit), cancellationToken);

    internal static bool MatchesIdentity(GameProcessRecord record, int pid, ulong startSeconds, ulong startMicroseconds) =>
        record.Pid == pid && record.StartSeconds == startSeconds && record.StartMicroseconds == startMicroseconds;

    /// <summary>
    /// Waits for the recorded process only while its PID and creation time still match the record,
    /// so a reused PID of an unrelated process is never awaited. Returns false when it already exited or was replaced.
    /// </summary>
    public static bool TryWaitForRecordedProcess(GameProcessRecord record, out int? exitCode)
    {
        ArgumentNullException.ThrowIfNull(record);
        exitCode = null;
        using var process = Win32Process.Open((uint)record.Pid);
        if (process.IsInvalid
            || !Win32Process.TryReadCreationTime(process, out var seconds, out var microseconds)
            || !MatchesIdentity(record, record.Pid, seconds, microseconds)
            || Win32Process.Wait(process, uint.MaxValue) != 0)
        {
            return false;
        }

        exitCode = unchecked((int)Win32Process.GetExitCode(process));
        return true;
    }

    private static (bool Success, ulong StartSeconds, ulong StartMicroseconds) ReadCreationTime(SafeProcessHandle process)
    {
        var success = Win32Process.TryReadCreationTime(process, out var seconds, out var microseconds);
        return (success, seconds, microseconds);
    }

    internal static string FormatArgumentFileValue(string value)
    {
        if (value.Contains('\0') || value.Contains('\r') || value.Contains('\n'))
        {
            throw new MinecraftLaunchException(MinecraftLaunchError.ArgumentFileEncoding);
        }

        // The JDK argument file parser treats ' as a quote and # at the start of a token as a comment.
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character is '"' or '\\' or '\'' or '#'))
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Makes the executable and arguments representable in the ANSI code page java.exe reads them with,
    /// replacing unrepresentable paths with 8.3 names. Java 8 receives them on the command line, so the whole
    /// command line is checked as well.
    /// </summary>
    internal static (string Executable, IReadOnlyList<string> Arguments) PrepareArguments(
        MinecraftLaunchPlan plan,
        uint codePage,
        Func<string, string?>? shortPathProvider = null)
    {
        var executable = JavaAnsiEncoding.AdaptPaths(plan.Executable, codePage, shortPathProvider);
        var arguments = plan.Arguments.Select(argument => JavaAnsiEncoding.AdaptPaths(argument, codePage, shortPathProvider)).ToArray();
        if (plan.JavaMajorVersion < 9)
        {
            _ = JavaAnsiEncoding.Encode(Win32Process.BuildCommandLine(executable, arguments), codePage);
        }

        return (executable, arguments);
    }

    /// <summary>
    /// Creates the folders substituted into launch arguments. 8.3 names exist only for existing paths, so a
    /// missing folder under a non-ANSI root would otherwise fail <see cref="PrepareArguments"/>.
    /// </summary>
    internal static void EnsureLaunchDirectories(string instanceRoot)
    {
        foreach (var relative in new[] { "minecraft/natives", "minecraft/libraries", "minecraft/assets", "minecraft/logs" })
        {
            System.IO.Directory.CreateDirectory(InstanceStorage.ContainedPath(relative, instanceRoot, allowBaseReparsePoint: true));
        }
    }

    internal static byte[] EncodeArgumentFile(IReadOnlyList<string> arguments, uint codePage) =>
        JavaAnsiEncoding.Encode(string.Join("\r\n", arguments.Select(FormatArgumentFileValue)) + "\r\n", codePage);

    private static GameProcessLaunch Start(
        MinecraftLaunchPlan plan,
        Guid instanceId,
        string instanceRoot,
        Guid? sessionId,
        uint codePage,
        CancellationToken cancellationToken,
        Action<int?>? onExit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLaunchDirectories(instanceRoot);
        var (executable, javaArguments) = PrepareArguments(plan, codePage);
        string? argumentFile = null;
        if (plan.JavaMajorVersion >= 9)
        {
            argumentFile = WriteArgumentFile(instanceRoot, javaArguments, codePage);
            try
            {
                javaArguments = [JavaAnsiEncoding.AdaptPaths("@" + argumentFile, codePage)];
            }
            catch
            {
                TryDelete(argumentFile);
                throw;
            }
        }

        var logPath = InstanceStorage.ContainedPath("minecraft/logs/hako-launch.log", instanceRoot, allowBaseReparsePoint: true);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        logPath = InstanceStorage.ContainedPath("minecraft/logs/hako-launch.log", instanceRoot, allowBaseReparsePoint: true);

        FileStream? log = null;
        SafeProcessHandle? process = null;
        try
        {
            log = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096,
                FileOptions.SequentialScan);
            WindowsFileSecurity.RestrictToCurrentUser(logPath);
            cancellationToken.ThrowIfCancellationRequested();

            var processInfo = Win32Process.Start(executable, javaArguments, plan.WorkingDirectory, log.SafeFileHandle);
            process = processInfo.Process;
            log.Dispose();
            log = null;

            if (!Win32Process.TryReadCreationTime(process, out var startSeconds, out var startMicroseconds))
            {
                Win32Process.Terminate(process, 1);
                throw new MinecraftLaunchException(MinecraftLaunchError.ProcessStartFailed);
            }

            var record = new GameProcessRecord(instanceId, checked((int)processInfo.ProcessId), startSeconds, startMicroseconds, sessionId);
            try
            {
                WriteRecord(instanceRoot, record);
            }
            catch
            {
                Win32Process.Terminate(process, 1);
                throw;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Win32Process.Terminate(process, 1);
                RemoveRecordIfCurrent(instanceRoot, record);
                throw new OperationCanceledException(cancellationToken);
            }

            var completion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = ObserveAsync(record, instanceRoot, process, argumentFile, completion, onExit);
            process = null;
            return new GameProcessLaunch(record, logPath, completion.Task);
        }
        catch
        {
            process?.Dispose();
            log?.Dispose();
            if (argumentFile is not null)
            {
                TryDelete(argumentFile);
            }

            throw;
        }
    }

    private static GameProcessLaunch? Restore(
        Guid instanceId,
        string instanceRoot,
        Func<SafeProcessHandle, (bool Success, ulong StartSeconds, ulong StartMicroseconds)> creationTimeProvider,
        CancellationToken cancellationToken,
        Action<int?>? onExit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GameProcessRecord? record;
        try
        {
            record = ReadRecord(instanceRoot);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new MinecraftLaunchException(MinecraftLaunchError.ProcessRecordInvalid, innerException: exception);
        }

        if (record is null)
        {
            DeleteArgumentFiles(instanceRoot);
            return null;
        }

        if (record.InstanceID != instanceId || record.Pid <= 0)
        {
            RemoveRecordIfCurrent(instanceRoot, record);
            DeleteArgumentFiles(instanceRoot);
            return null;
        }

        var process = Win32Process.Open((uint)record.Pid);
        var (timeRead, seconds, microseconds) = process.IsInvalid
            ? (false, 0UL, 0UL)
            : creationTimeProvider(process);
        if (!timeRead || !MatchesIdentity(record, record.Pid, seconds, microseconds))
        {
            process.Dispose();
            RemoveRecordIfCurrent(instanceRoot, record);
            DeleteArgumentFiles(instanceRoot);
            return null;
        }

        var waitResult = Win32Process.Wait(process, 0);
        if (waitResult == 0)
        {
            process.Dispose();
            RemoveRecordIfCurrent(instanceRoot, record);
            DeleteArgumentFiles(instanceRoot);
            return null;
        }

        if (waitResult != 0x00000102)
        {
            process.Dispose();
            throw new Win32Exception("WaitForSingleObject could not inspect the saved Minecraft process.");
        }

        var completion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ObserveAsync(record, instanceRoot, process, null, completion, onExit);
        var logPath = InstanceStorage.ContainedPath("minecraft/logs/hako-launch.log", instanceRoot, allowBaseReparsePoint: true);
        return new GameProcessLaunch(record, logPath, completion.Task);
    }

    // Registers the process handle with the thread pool wait thread instead of blocking a worker
    // for the whole game session.
    private static async Task WaitForExitAsync(SafeProcessHandle process)
    {
        using var waitHandle = new ProcessWaitHandle(process);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle, static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), exited,
            Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await exited.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    private static async Task ObserveAsync(
        GameProcessRecord record,
        string instanceRoot,
        SafeProcessHandle process,
        string? argumentFile,
        TaskCompletionSource<int?> completion,
        Action<int?>? onExit)
    {
        try
        {
            await WaitForExitAsync(process).ConfigureAwait(false);
            if (Win32Process.Wait(process, 0) != 0)
            {
                throw new Win32Exception("WaitForSingleObject failed while observing Minecraft.");
            }

            var exitCode = unchecked((int)Win32Process.GetExitCode(process));
            if (argumentFile is not null)
            {
                TryDelete(argumentFile);
            }
            else
            {
                DeleteArgumentFiles(instanceRoot);
            }

            RemoveRecordIfCurrent(instanceRoot, record);
            completion.TrySetResult(exitCode);
            try { onExit?.Invoke(exitCode); }
            catch (Exception) { }
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
            try { onExit?.Invoke(null); }
            catch (Exception) { }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string WriteArgumentFile(string instanceRoot, IReadOnlyList<string> arguments, uint codePage)
    {
        var bytes = EncodeArgumentFile(arguments, codePage);
        var launchDirectory = InstanceStorage.ContainedPath("minecraft/.hako-launch", instanceRoot, allowBaseReparsePoint: true);
        System.IO.Directory.CreateDirectory(launchDirectory);
        _ = InstanceStorage.ContainedPath("minecraft/.hako-launch", instanceRoot, allowBaseReparsePoint: true);
        var path = InstanceStorage.ContainedPath($"minecraft/.hako-launch/{Guid.NewGuid():N}.args", instanceRoot, allowBaseReparsePoint: true);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            WindowsFileSecurity.RestrictToCurrentUser(path);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static void WriteRecord(string instanceRoot, GameProcessRecord record)
    {
        var path = InstanceStorage.ContainedPath("minecraft/.hako-running.json", instanceRoot, allowBaseReparsePoint: true);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record, HakoJsonContext.Default.GameProcessRecord);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static GameProcessRecord? ReadRecord(string instanceRoot)
    {
        var path = InstanceStorage.ContainedPath("minecraft/.hako-running.json", instanceRoot, allowBaseReparsePoint: true);
        if (!File.Exists(path))
        {
            return null;
        }

        if (new FileInfo(path).Length > 64 * 1024)
        {
            throw new JsonException("The running game record is too large.");
        }

        var record = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(path), HakoJsonContext.Default.GameProcessRecord);
        if (record is null || record.InstanceID == Guid.Empty || record.Pid <= 0
            || record.StartSeconds == 0 || record.StartMicroseconds >= 1_000_000)
        {
            throw new JsonException("The running game record is invalid.");
        }

        return record;
    }

    private static void RemoveRecordIfCurrent(string instanceRoot, GameProcessRecord record)
    {
        var current = ReadRecord(instanceRoot);
        if (current == record)
        {
            var path = InstanceStorage.ContainedPath("minecraft/.hako-running.json", instanceRoot, allowBaseReparsePoint: true);
            TryDelete(path);
        }
    }

    private static void DeleteArgumentFiles(string instanceRoot)
    {
        var directory = InstanceStorage.ContainedPath("minecraft/.hako-launch", instanceRoot, allowBaseReparsePoint: true);
        if (!System.IO.Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*.args", SearchOption.TopDirectoryOnly))
        {
            var safe = InstanceStorage.ContainedPath("minecraft/.hako-launch/" + Path.GetFileName(path), instanceRoot, allowBaseReparsePoint: true);
            TryDelete(safe);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
