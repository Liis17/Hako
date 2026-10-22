using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Hako.Core.Minecraft;

public enum JavaValidationError
{
    MissingExecutable,
    ProbeFailed,
    ProbeTimeout,
    UnsupportedVersion,
    ArchitectureMismatch
}

public sealed class JavaLaunchValidationException(JavaValidationError error, Exception? innerException = null)
    : Exception("The selected Java runtime cannot launch this build.", innerException)
{
    public JavaValidationError Error { get; } = error;
    public string ResourceKey => Error switch
    {
        JavaValidationError.MissingExecutable => "LaunchJavaMissing",
        JavaValidationError.ProbeFailed => "LaunchJavaProbeFailed",
        JavaValidationError.ProbeTimeout => "LaunchJavaProbeTimeout",
        JavaValidationError.UnsupportedVersion => "LaunchJavaVersionMismatch",
        JavaValidationError.ArchitectureMismatch => "LaunchJavaArchitectureMismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(Error), Error, null)
    };
}

public sealed class JavaLaunchValidation
{
    private const int MaximumOutputBytes = 64 * 1024;
    private static readonly string[] RemovedEnvironmentVariables = ["JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS"];
    private readonly IJavaValidationProbe _probe;

    public JavaLaunchValidation() : this(new ProcessJavaValidationProbe()) { }

    internal JavaLaunchValidation(IJavaValidationProbe probe) => _probe = probe;

    public async Task<int> ValidateAsync(
        string executable,
        int minimumMajor,
        GameArchitecture architecture,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new JavaLaunchValidationException(JavaValidationError.MissingExecutable);
        }

        JavaProbeResult result;
        try
        {
            result = await _probe.RunAsync(executable, ["-XshowSettings:properties", "-version"],
                EnvironmentForValidation(), timeout ?? TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new JavaLaunchValidationException(JavaValidationError.ProbeTimeout, exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            throw new JavaLaunchValidationException(JavaValidationError.ProbeFailed, exception);
        }

        if (result.ExitCode != 0)
        {
            throw new JavaLaunchValidationException(JavaValidationError.ProbeFailed);
        }

        var properties = Encoding.UTF8.GetString(result.Output).Split('\n')
            .Select(line => line.Split('=', 2).Select(value => value.Trim()).ToArray())
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        var version = properties.GetValueOrDefault("java.version") ?? "";
        var majorText = version.StartsWith("1.", StringComparison.Ordinal)
            ? version[2..].Split('.', '_', '+')[0]
            : new string(version.TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(majorText, out var major) || major < minimumMajor)
        {
            throw new JavaLaunchValidationException(JavaValidationError.UnsupportedVersion);
        }

        var osArchitecture = properties.GetValueOrDefault("os.arch") ?? "";
        var compatible = architecture switch
        {
            GameArchitecture.Arm64 => osArchitecture is "aarch64" or "arm64",
            GameArchitecture.X64 => osArchitecture is "amd64" or "x86_64",
            GameArchitecture.X86 => osArchitecture is "x86" or "i386" or "i486" or "i586" or "i686",
            _ => false
        };
        if (!compatible)
        {
            throw new JavaLaunchValidationException(JavaValidationError.ArchitectureMismatch);
        }

        return major;
    }

    internal static IReadOnlyDictionary<string, string?> EnvironmentForValidation()
    {
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string)
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in RemovedEnvironmentVariables)
        {
            environment.Remove(name);
        }

        return environment;
    }
}

internal sealed record JavaProbeResult(int ExitCode, byte[] Output);

internal interface IJavaValidationProbe
{
    Task<JavaProbeResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class ProcessJavaValidationProbe : IJavaValidationProbe
{
    private const int MaximumOutputBytes = 64 * 1024;

    public async Task<JavaProbeResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), "hako-java-probe-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            await using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            WindowsFileSecurity.RestrictToCurrentUser(temporaryPath);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.Environment.Clear();
            foreach (var (name, value) in environment)
            {
                if (value is not null)
                {
                    process.StartInfo.Environment[name] = value;
                }
            }

            if (!process.Start())
            {
                throw new Win32Exception("The Java probe process did not start.");
            }

            var writeGate = new SemaphoreSlim(1, 1);
            long written = 0;
            var standardOutput = PumpAsync(process.StandardOutput.BaseStream, output, writeGate, () => written, value => written = value, cancellationToken);
            var standardError = PumpAsync(process.StandardError.BaseStream, output, writeGate, () => written, value => written = value, cancellationToken);
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
            try
            {
                await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }

                await DrainAsync(standardOutput, standardError).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new TimeoutException();
            }

            await DrainAsync(standardOutput, standardError).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            await using var input = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var captured = new byte[Math.Min((int)input.Length, MaximumOutputBytes)];
            var read = await input.ReadAsync(captured, cancellationToken).ConfigureAwait(false);
            return new JavaProbeResult(process.ExitCode, captured.AsSpan(0, read).ToArray());
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // A descendant of the probe may still hold the pipe after Java exits; never wait for it indefinitely.
    private static async Task DrainAsync(Task standardOutput, Task standardError)
    {
        try
        {
            await Task.WhenAll(standardOutput, standardError).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private static async Task PumpAsync(
        Stream input,
        FileStream output,
        SemaphoreSlim writeGate,
        Func<long> getWritten,
        Action<long> setWritten,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }

            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var written = getWritten();
                var remaining = MaximumOutputBytes - written;
                if (remaining > 0)
                {
                    var writeLength = (int)Math.Min(count, remaining);
                    await output.WriteAsync(buffer.AsMemory(0, writeLength), cancellationToken).ConfigureAwait(false);
                    setWritten(written + writeLength);
                }
            }
            finally
            {
                writeGate.Release();
            }
        }
    }
}

internal static partial class WindowsFileSecurity
{
    internal static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;FA;;;OW)", 1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!SetFileSecurityW(path, 0x00000004, descriptor))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSDRevision,
        out nint securityDescriptor,
        out uint securityDescriptorSize);

    [LibraryImport("advapi32.dll", EntryPoint = "SetFileSecurityW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetFileSecurityW(string fileName, uint securityInformation, nint securityDescriptor);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial nint LocalFree(nint memory);
}
