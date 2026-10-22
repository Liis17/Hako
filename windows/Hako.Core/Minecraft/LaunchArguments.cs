using System.Runtime.InteropServices;

namespace Hako.Core.Minecraft;

public static class LaunchArguments
{
    public static IReadOnlyList<string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<string>();
        var value = new System.Text.StringBuilder();
        char quote = '\0';
        var escaped = false;
        var started = false;

        foreach (var character in text)
        {
            if (escaped)
            {
                value.Append(character);
                escaped = false;
                started = true;
            }
            else if (character == '\\' && quote != '\'')
            {
                escaped = true;
                started = true;
            }
            else if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    value.Append(character);
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
                started = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (started)
                {
                    result.Add(value.ToString());
                    value.Clear();
                    started = false;
                }
            }
            else
            {
                value.Append(character);
                started = true;
            }
        }

        if (quote != '\0' || escaped)
        {
            throw new LaunchArgumentException("LaunchArgumentsInvalid");
        }

        if (started)
        {
            result.Add(value.ToString());
        }

        return result;
    }

    public static string Format(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(argument =>
    {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character is '"' or '\'' or '\\'))
        {
            return argument;
        }

        return "\"" + argument.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }));

    public static IReadOnlyList<string> ApplyMemory(
        IEnumerable<string> arguments,
        int maximumMiB,
        GameArchitecture architecture,
        JavaMemoryPolicy? policy = null)
    {
        var memory = policy ?? JavaMemoryPolicy.Current;
        var maximum = memory.Normalize(maximumMiB, architecture);
        var maximumBytes = (long)maximum * 1024 * 1024;
        var source = arguments.ToArray();
        var result = new List<string>(source.Length + 1);

        for (var index = 0; index < source.Length; index++)
        {
            var argument = source[index];
            if (argument == "-Xmx")
            {
                if (index + 1 < source.Length && (source[index + 1].Length == 0 || source[index + 1][0] != '-'))
                {
                    index++;
                }

                continue;
            }

            if (argument.StartsWith("-Xmx", StringComparison.Ordinal)
                || argument.StartsWith("-XX:MaxHeapSize=", StringComparison.Ordinal))
            {
                continue;
            }

            if (argument == "-Xms" && index + 1 < source.Length)
            {
                var initial = source[++index];
                result.Add(HeapBytes(initial) is { } bytes && bytes > maximumBytes
                    ? $"-Xms{maximum}M"
                    : "-Xms" + initial);
                continue;
            }

            if (argument.StartsWith("-Xms", StringComparison.Ordinal)
                && HeapBytes(argument[4..]) is { } xmsBytes && xmsBytes > maximumBytes)
            {
                result.Add($"-Xms{maximum}M");
                continue;
            }

            if (argument.StartsWith("-XX:InitialHeapSize=", StringComparison.Ordinal)
                && HeapBytes(argument[20..]) is { } initialBytes && initialBytes > maximumBytes)
            {
                result.Add($"-XX:InitialHeapSize={maximum}M");
                continue;
            }

            result.Add(argument);
        }

        result.Add($"-Xmx{maximum}M");
        return result;
    }

    public static long? HeapBytes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var suffix = char.ToLowerInvariant(value[^1]);
        var multiplier = suffix switch
        {
            'k' => 1024L,
            'm' => 1024L * 1024,
            'g' => 1024L * 1024 * 1024,
            _ => 1L
        };
        var numberText = multiplier == 1 ? value : value[..^1];
        if (!long.TryParse(numberText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) || number <= 0
            || number > long.MaxValue / multiplier)
        {
            return null;
        }

        return number * multiplier;
    }
}

public sealed class LaunchArgumentException(string resourceKey) : Exception("The launch arguments are invalid.")
{
    public string ResourceKey { get; } = resourceKey;
}

public sealed partial class JavaMemoryPolicy
{
    private const long MiB = 1024L * 1024;
    private readonly ulong _physicalBytes;

    public JavaMemoryPolicy(ulong physicalBytes) => _physicalBytes = physicalBytes;

    public static JavaMemoryPolicy Current => new(ReadPhysicalMemoryBytes());

    public int MaximumMiB(GameArchitecture architecture)
    {
        var physicalMiB = _physicalBytes / (ulong)MiB;
        var maximum = (int)Math.Min(int.MaxValue, physicalMiB / 256 * 256);
        maximum = Math.Max(512, maximum);
        return architecture == GameArchitecture.X86 ? Math.Min(maximum, 1536) : maximum;
    }

    public int InitialMiB(GameArchitecture architecture) => Normalize(Math.Min(4096, MaximumMiB(architecture) / 4), architecture);

    public int Normalize(int value, GameArchitecture architecture)
    {
        var maximum = MaximumMiB(architecture);
        if (value <= 0)
        {
            value = InitialMiB(architecture);
        }

        return Math.Min(maximum, Math.Max(512, value / 256 * 256));
    }

    public bool Warns(int value) => (double)value * MiB > (double)_physicalBytes * 0.7;

    private static ulong ReadPhysicalMemoryBytes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 16UL * 1024 * 1024 * 1024;
        }

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status) == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        return status.TotalPhysical;
    }

    [StructLayout(LayoutKind.Sequential)]
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

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    private static partial int GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
