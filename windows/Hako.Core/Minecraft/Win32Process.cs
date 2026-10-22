using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Hako.Core.Minecraft;

internal static unsafe partial class Win32Process
{
    internal const int MaximumCommandLineCharacters = 32767;
    internal const uint CreateNoWindow = 0x08000000;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const nint ProcThreadAttributeHandleList = 0x00020002;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint Synchronize = 0x00100000;
    private const uint Infinite = 0xffffffff;
    private const ulong WindowsToUnixEpochTicks = 116444736000000000;

    internal static (SafeProcessHandle Process, uint ProcessId) Start(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        SafeFileHandle logHandle)
    {
        var commandLine = BuildCommandLine(executable, arguments);
        var environmentBlock = BuildEnvironmentBlock();
        var environmentMemory = Marshal.StringToHGlobalUni(environmentBlock);
        nint attributeList = 0;
        var attributeListInitialized = false;
        var inheritedHandle = logHandle.DangerousGetHandle();
        try
        {
            if (!SetHandleInformation(inheritedHandle, HandleFlagInherit, HandleFlagInherit))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            // Only the log handle may be inherited: other inheritable handles of Hako (for example pipes
            // of a concurrent Java probe) must not leak into a game that outlives the launcher.
            // Known limitation: while the log handle is marked inheritable, a concurrent Process.Start without
            // a handle list (for example a Java probe) may inherit it for that child's lifetime.
            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal((nint)attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            attributeListInitialized = true;
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList, &inheritedHandle,
                    (nuint)sizeof(nint), 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StdOutput = inheritedHandle,
                    StdError = inheritedHandle
                },
                AttributeList = attributeList
            };
            ProcessInformation information;
            // CreateProcessW may write into the command line, so it gets a private null-terminated copy.
            var commandLineBuffer = new char[commandLine.Length + 1];
            commandLine.CopyTo(commandLineBuffer);
            fixed (char* mutableCommandLine = commandLineBuffer)
            {
                if (!CreateProcessW(executable, mutableCommandLine, 0, 0, 1,
                    CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent, environmentMemory,
                    workingDirectory, ref startup, out information))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            _ = CloseHandle(information.Thread);
            return (new SafeProcessHandle(information.Process, ownsHandle: true), information.ProcessId);
        }
        finally
        {
            if (attributeListInitialized)
            {
                DeleteProcThreadAttributeList(attributeList);
            }

            if (attributeList != 0)
            {
                Marshal.FreeHGlobal(attributeList);
            }

            Marshal.FreeHGlobal(environmentMemory);
            _ = SetHandleInformation(inheritedHandle, HandleFlagInherit, 0);
        }
    }

    internal static SafeProcessHandle Open(uint processId) =>
        OpenProcess(ProcessQueryLimitedInformation | Synchronize, 0, processId);

    internal static bool TryReadCreationTime(SafeProcessHandle process, out ulong startSeconds, out ulong startMicroseconds)
    {
        startSeconds = 0;
        startMicroseconds = 0;
        if (process.IsInvalid || process.IsClosed || !GetProcessTimes(process, out var creation, out _, out _, out _))
        {
            return false;
        }

        var ticks = ((ulong)creation.High << 32) | creation.Low;
        if (ticks < WindowsToUnixEpochTicks)
        {
            return false;
        }

        var unixMicroseconds = (ticks - WindowsToUnixEpochTicks) / 10;
        startSeconds = unixMicroseconds / 1_000_000;
        startMicroseconds = unixMicroseconds % 1_000_000;
        return true;
    }

    internal static uint Wait(SafeProcessHandle process, uint milliseconds) => WaitForSingleObject(process, milliseconds);

    internal static uint GetExitCode(SafeProcessHandle process)
    {
        if (!GetExitCodeProcess(process, out var exitCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return exitCode;
    }

    internal static void Terminate(SafeProcessHandle process, uint exitCode)
    {
        if (!process.IsInvalid && !process.IsClosed)
        {
            _ = TerminateProcess(process, exitCode);
            _ = WaitForSingleObject(process, Infinite);
        }
    }

    internal static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var commandLine = string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteArgument));
        if (commandLine.Length + 1 > MaximumCommandLineCharacters)
        {
            throw new MinecraftLaunchException(MinecraftLaunchError.CommandLineTooLong);
        }

        return commandLine;
    }

    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    private static string BuildEnvironmentBlock()
    {
        var entries = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string name
                && !string.Equals(name, "JAVA_TOOL_OPTIONS", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "_JAVA_OPTIONS", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "JDK_JAVA_OPTIONS", StringComparison.OrdinalIgnoreCase))
            .Select(entry => (Name: (string)entry.Key, Value: entry.Value?.ToString() ?? ""))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Name + "=" + entry.Value);
        return string.Join('\0', entries) + "\0\0";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Length;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        int inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "InitializeProcThreadAttributeList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, int flags, ref nuint size);

    [LibraryImport("kernel32.dll", EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        nint attributeList, uint flags, nint attribute, void* value, nuint size, nint previousValue, nint returnSize);

    [LibraryImport("kernel32.dll", EntryPoint = "DeleteProcThreadAttributeList")]
    private static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "SetHandleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

}
