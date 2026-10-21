using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Hako.Core;

public sealed record GameLaunchDefaults(
    string JavaPath,
    string JavaArguments,
    string MinecraftArguments,
    bool Fullscreen,
    int WindowWidth,
    int WindowHeight,
    int MaximumMemoryMiB)
{
    private static readonly Lazy<int> InitialMemory = new(GetInitialMemoryMiB);

    public static GameLaunchDefaults Standard => new("", "", "", false, 1280, 720, InitialMemory.Value);

    public static GameLaunchDefaults From(UserSettings? settings)
    {
        var standard = Standard;
        return new GameLaunchDefaults(
            settings?.JavaPath ?? standard.JavaPath,
            settings?.JavaArguments ?? standard.JavaArguments,
            settings?.MinecraftArguments ?? standard.MinecraftArguments,
            settings?.Fullscreen ?? standard.Fullscreen,
            settings?.WindowWidth is > 0 ? settings.WindowWidth.Value : standard.WindowWidth,
            settings?.WindowHeight is > 0 ? settings.WindowHeight.Value : standard.WindowHeight,
            NormalizeMemory(settings?.MaximumMemoryMiB ?? standard.MaximumMemoryMiB));
    }

    public static int NormalizeMemory(int value)
    {
        if (value <= 0)
        {
            return GetInitialMemoryMiB();
        }

        var maximum = GetMaximumMemoryMiB();
        return Math.Min(maximum, Math.Max(512, value / 256 * 256));
    }

    private static int GetInitialMemoryMiB() => NormalizeMemory(Math.Min(4096, GetMaximumMemoryMiB() / 4));

    private static int GetMaximumMemoryMiB()
    {
        var physicalBytes = GetPhysicalMemoryBytes();
        return Math.Max(512, checked((int)(physicalBytes / (1024 * 1024)) / 256 * 256));
    }

    private static ulong GetPhysicalMemoryBytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 16UL * 1024 * 1024 * 1024;
        }

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return status.TotalPhysical;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
