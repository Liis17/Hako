using System.Runtime.InteropServices;
using System.Text;

namespace Hako.Core.Minecraft;

/// <summary>
/// java.exe on Windows is an ANSI program: it reads its command line through GetCommandLineA and
/// decodes argument files as raw bytes in sun.jnu.encoding, which stays the ANSI code page after JEP 400.
/// Launch arguments therefore must be representable in GetACP() without best-fit substitutions.
/// </summary>
internal static unsafe partial class JavaAnsiEncoding
{
    internal const uint Utf8CodePage = 65001;
    private const uint NoBestFitCharacters = 0x00000400;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static uint CurrentCodePage => GetACP();

    internal static bool TryEncode(string value, uint codePage, out byte[] bytes)
    {
        bytes = [];
        if (value.Length == 0)
        {
            return true;
        }

        if (codePage == Utf8CodePage)
        {
            try
            {
                bytes = StrictUtf8.GetBytes(value);
                return true;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }

        fixed (char* source = value)
        {
            int usedDefault;
            var length = WideCharToMultiByte(codePage, NoBestFitCharacters, source, value.Length, null, 0, 0, &usedDefault);
            if (length <= 0 || usedDefault != 0)
            {
                return false;
            }

            var result = new byte[length];
            fixed (byte* destination = result)
            {
                usedDefault = 0;
                if (WideCharToMultiByte(codePage, NoBestFitCharacters, source, value.Length, destination, length, 0, &usedDefault) != length
                    || usedDefault != 0)
                {
                    return false;
                }
            }

            bytes = result;
            return true;
        }
    }

    internal static bool CanRepresent(string value, uint codePage) => TryEncode(value, codePage, out _);

    internal static byte[] Encode(string value, uint codePage) => TryEncode(value, codePage, out var bytes)
        ? bytes
        : throw new MinecraftLaunchException(MinecraftLaunchError.ArgumentFileEncoding);

    /// <summary>
    /// Replaces absolute paths that the code page can't represent with their 8.3 names. Paths may be the whole
    /// argument, the value of an <c>-Dname=</c> or <c>@file</c> argument, or entries of a <c>;</c>-separated list.
    /// </summary>
    internal static string AdaptPaths(string argument, uint codePage, Func<string, string?>? shortPathProvider = null)
    {
        if (CanRepresent(argument, codePage))
        {
            return argument;
        }

        shortPathProvider ??= GetShortPath;
        var prefixLength = argument.StartsWith('@') ? 1
            : argument.StartsWith('-') && argument.IndexOf('=') is var equals and >= 0 ? equals + 1
            : 0;
        var entries = argument[prefixLength..].Split(';');
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (CanRepresent(entry, codePage))
            {
                continue;
            }

            var shortPath = Path.IsPathFullyQualified(entry) ? shortPathProvider(entry) : null;
            if (shortPath is null || !CanRepresent(shortPath, codePage))
            {
                throw new MinecraftLaunchException(MinecraftLaunchError.ArgumentFileEncoding);
            }

            entries[index] = shortPath;
        }

        var adapted = argument[..prefixLength] + string.Join(';', entries);
        return CanRepresent(adapted, codePage)
            ? adapted
            : throw new MinecraftLaunchException(MinecraftLaunchError.ArgumentFileEncoding);
    }

    // Returns null when the path doesn't exist or the volume has no 8.3 names; the caller then rejects the launch.
    internal static string? GetShortPath(string path)
    {
        var buffer = new char[path.Length + 1];
        while (true)
        {
            uint length;
            fixed (char* destination = buffer)
            {
                length = GetShortPathNameW(path, destination, (uint)buffer.Length);
            }

            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }

            buffer = new char[length];
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetACP")]
    private static partial uint GetACP();

    [LibraryImport("kernel32.dll", EntryPoint = "WideCharToMultiByte", SetLastError = true)]
    private static partial int WideCharToMultiByte(
        uint codePage, uint flags, char* wideCharString, int wideCharCount, byte* multiByteString, int multiByteCount,
        nint defaultChar, int* usedDefaultChar);

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathNameW(string longPath, char* shortPath, uint bufferLength);
}
