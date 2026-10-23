using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Hako.Core.Minecraft;

namespace Hako.Core.Playtime;

public interface IPlaytimeClock
{
    ulong GetUnbiasedTicks();
}

public sealed partial class WindowsPlaytimeClock : IPlaytimeClock
{
    public ulong GetUnbiasedTicks()
    {
        if (!QueryUnbiasedInterruptTime(out var ticks)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return ticks;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryUnbiasedInterruptTime", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}

public interface IPlaytimeProcessProbe
{
    PlaytimeProcessIdentity? ReadCurrentIdentity();
    bool IsRunning(PlaytimeProcessIdentity identity);
    bool WaitForExitOrTimeout(PlaytimeProcessIdentity identity, TimeSpan timeout);
}

public sealed class WindowsPlaytimeProcessProbe : IPlaytimeProcessProbe
{
    public PlaytimeProcessIdentity? ReadCurrentIdentity() => ReadIdentity(Environment.ProcessId);

    public bool IsRunning(PlaytimeProcessIdentity identity)
    {
        using var process = Win32Process.Open((uint)identity.Pid);
        return Matches(process, identity) && Win32Process.Wait(process, 0) == 0x00000102;
    }

    public bool WaitForExitOrTimeout(PlaytimeProcessIdentity identity, TimeSpan timeout)
    {
        using var process = Win32Process.Open((uint)identity.Pid);
        if (!Matches(process, identity)) return true;
        var milliseconds = timeout.TotalMilliseconds >= uint.MaxValue - 1
            ? uint.MaxValue - 1
            : (uint)Math.Max(0, timeout.TotalMilliseconds);
        return Win32Process.Wait(process, milliseconds) != 0x00000102;
    }

    internal static PlaytimeProcessIdentity? ReadIdentity(int pid)
    {
        if (pid <= 0) return null;
        using var process = Win32Process.Open((uint)pid);
        return !process.IsInvalid && Win32Process.TryReadCreationTime(process, out var seconds, out var microseconds)
            ? new PlaytimeProcessIdentity(pid, seconds, microseconds)
            : null;
    }

    private static bool Matches(SafeProcessHandle process, PlaytimeProcessIdentity identity) =>
        !process.IsInvalid && Win32Process.TryReadCreationTime(process, out var seconds, out var microseconds)
        && seconds == identity.StartSeconds && microseconds == identity.StartMicroseconds;
}

public interface IPlaytimeHelperLauncher
{
    Task<bool> TryStartAsync(string journalPath, CancellationToken cancellationToken = default);
}

public static class PlaytimeHelperHost
{
    public static int Run(string[] arguments, IPlaytimeClock? clock = null, IPlaytimeProcessProbe? processProbe = null,
        TimeSpan? checkpointInterval = null)
    {
        if (arguments.Length != 1 || !PlaytimePaths.TryValidateHelperPath(arguments[0], out var path, out var sessionID)) return 2;
        clock ??= new WindowsPlaytimeClock();
        processProbe ??= new WindowsPlaytimeProcessProbe();
        var interval = checkpointInterval ?? TimeSpan.FromSeconds(10);
        if (interval <= TimeSpan.Zero) return 2;

        try
        {
            using var sessionLock = PlaytimeJournalLock.TryAcquire(Path.ChangeExtension(path, ".lock"));
            if (sessionLock is null) return 0;
            var journal = PlaytimeJson.ReadJournal(path);
            if (journal.SessionID != sessionID || journal.Process is not { } process || journal.IsFinished) return 2;
            var identity = new PlaytimeProcessIdentity(process.Pid, process.StartSeconds, process.StartMicroseconds);
            var helper = processProbe.ReadCurrentIdentity();
            if (helper is null) return 3;
            journal.Helper = helper;

            if (!processProbe.IsRunning(identity))
            {
                // No observation of the original process: only its saved checkpoint is trustworthy.
                journal.IsFinished = true;
                PlaytimeJson.AtomicWrite(path, PlaytimeJson.SerializeJournal(journal));
                return 0;
            }

            PlaytimeJson.AtomicWrite(path, PlaytimeJson.SerializeJournal(journal));
            while (true)
            {
                if (processProbe.WaitForExitOrTimeout(identity, interval))
                {
                    journal.ElapsedSeconds = Math.Max(journal.ElapsedSeconds,
                        journal.BaseElapsedSeconds + Elapsed(journal.StartedUnbiasedTicks, clock.GetUnbiasedTicks()));
                    journal.IsFinished = true;
                    PlaytimeJson.AtomicWrite(path, PlaytimeJson.SerializeJournal(journal));
                    return 0;
                }

                journal.ElapsedSeconds = Math.Max(journal.ElapsedSeconds,
                    journal.BaseElapsedSeconds + Elapsed(journal.StartedUnbiasedTicks, clock.GetUnbiasedTicks()));
                PlaytimeJson.AtomicWrite(path, PlaytimeJson.SerializeJournal(journal));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or Win32Exception or ArgumentException)
        {
            return 3;
        }
    }

    internal static double Elapsed(ulong started, ulong current) => current <= started ? 0 : (current - started) / 10_000_000d;
}
