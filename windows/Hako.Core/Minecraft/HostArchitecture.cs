using System.Runtime.InteropServices;

namespace Hako.Core.Minecraft;

public enum HostMachineArchitecture
{
    X86,
    X64,
    Arm64,
    Unknown
}

public sealed record HostArchitecture(HostMachineArchitecture Machine, bool Amd64UserEnabled);

public interface IHostArchitectureProvider
{
    HostArchitecture GetHostArchitecture();
}

public sealed class WindowsHostArchitectureProvider : IHostArchitectureProvider
{
    private const ushort ImageFileMachineUnknown = 0x0000;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xaa64;
    private const uint MachineTypeAttributeUserEnabled = 0x00000001;

    public HostArchitecture GetHostArchitecture()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Minecraft Windows installations require Windows.");
        }

        if (!IsWow64Process2(GetCurrentProcess(), out _, out var nativeMachine))
        {
            throw new InvalidOperationException($"Could not determine the Windows machine architecture (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        var machine = nativeMachine switch
        {
            ImageFileMachineI386 => HostMachineArchitecture.X86,
            ImageFileMachineAmd64 => HostMachineArchitecture.X64,
            ImageFileMachineArm64 => HostMachineArchitecture.Arm64,
            ImageFileMachineUnknown => HostMachineArchitecture.Unknown,
            _ => HostMachineArchitecture.Unknown
        };

        var amd64UserEnabled = machine == HostMachineArchitecture.Arm64 && IsAmd64UserEnabled();
        return new HostArchitecture(machine, amd64UserEnabled);
    }

    internal delegate int MachineTypeAttributesQuery(ushort machine, out uint attributes);

    private static bool IsAmd64UserEnabled() => IsAmd64UserEnabled(GetMachineTypeAttributes);

    internal static bool IsAmd64UserEnabled(MachineTypeAttributesQuery query)
    {
        try
        {
            var hResult = query(ImageFileMachineAmd64, out var attributes);
            return hResult >= 0 && (attributes & MachineTypeAttributeUserEnabled) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static int QueryMachineTypeAttributes(ushort machine, out uint attributes) =>
        GetMachineTypeAttributes(machine, out attributes);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetMachineTypeAttributes(ushort machine, out uint machineTypeAttributes);
}

public enum GameArchitecture
{
    X64,
    Arm64,
    X86
}

public static class GameArchitectureNames
{
    public static string MojangRuntimeKey(this GameArchitecture architecture) => architecture switch
    {
        GameArchitecture.X64 => "windows-x64",
        GameArchitecture.Arm64 => "windows-arm64",
        GameArchitecture.X86 => "windows-x86",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture))
    };

    public static string MojangNativeClassifier(this GameArchitecture architecture) => architecture switch
    {
        GameArchitecture.X64 => "natives-windows",
        GameArchitecture.Arm64 => "natives-windows-arm64",
        GameArchitecture.X86 => "natives-windows-x86",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture))
    };

    public static string RuleArchitecture(this GameArchitecture architecture) => architecture switch
    {
        GameArchitecture.X64 => "x86_64",
        GameArchitecture.Arm64 => "aarch64",
        GameArchitecture.X86 => "x86",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture))
    };

    internal static string NativeArchSubstitution(this GameArchitecture architecture) => architecture switch
    {
        GameArchitecture.X64 => "64",
        GameArchitecture.Arm64 => "arm64",
        GameArchitecture.X86 => "32",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture))
    };
}
