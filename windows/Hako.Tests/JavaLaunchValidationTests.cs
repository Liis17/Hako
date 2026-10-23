using System.Diagnostics;
using System.Text;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
public sealed class JavaLaunchValidationTests
{
    [TestMethod]
    public async Task FakeJavaScriptReportsVersionAndPinnedArchitecture()
    {
        using var temporary = new TemporaryDirectory();
        var script = CreateJavaScript(temporary.Root, "@echo off\necho java.version = 17.0.10\necho os.arch = amd64\n");
        var validation = new JavaLaunchValidation(new ScriptJavaProbe());

        var major = await validation.ValidateAsync(script, 17, GameArchitecture.X64, TimeSpan.FromSeconds(2));

        Assert.AreEqual(17, major);
        await Assert.ThrowsExactlyAsync<JavaLaunchValidationException>(() =>
            validation.ValidateAsync(script, 18, GameArchitecture.X64, TimeSpan.FromSeconds(2)));
        var mismatch = await Assert.ThrowsExactlyAsync<JavaLaunchValidationException>(() =>
            validation.ValidateAsync(script, 17, GameArchitecture.Arm64, TimeSpan.FromSeconds(2)));
        Assert.AreEqual(JavaValidationError.ArchitectureMismatch, mismatch.Error);
    }

    [TestMethod]
    public async Task FakeJavaTimeoutIsReportedAndRemovedJavaOptionsAreNotPassedToProbe()
    {
        using var temporary = new TemporaryDirectory();
        var script = CreateJavaScript(temporary.Root, "@echo off\n:again\ngoto again\n");
        var probe = new ScriptJavaProbe();
        var validation = new JavaLaunchValidation(probe);

        var error = await Assert.ThrowsExactlyAsync<JavaLaunchValidationException>(() =>
            validation.ValidateAsync(script, 17, GameArchitecture.X64, TimeSpan.FromMilliseconds(150)));

        Assert.AreEqual(JavaValidationError.ProbeTimeout, error.Error);
        Assert.IsFalse(probe.LastEnvironment!.Keys.Any(key => key is "JAVA_TOOL_OPTIONS" or "_JAVA_OPTIONS" or "JDK_JAVA_OPTIONS"));
    }

    [TestMethod]
    public async Task MissingJavaExecutableHasStructuredError()
    {
        var validation = new JavaLaunchValidation(new ScriptJavaProbe());
        var error = await Assert.ThrowsExactlyAsync<JavaLaunchValidationException>(() =>
            validation.ValidateAsync("missing-java.exe", 8, GameArchitecture.X64));

        Assert.AreEqual(JavaValidationError.MissingExecutable, error.Error);
        Assert.AreEqual("LaunchJavaMissing", error.ResourceKey);
    }

    private static string CreateJavaScript(string root, string contents)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "fake java.cmd");
        File.WriteAllText(path, contents, Encoding.ASCII);
        return path;
    }

    private sealed class ScriptJavaProbe : IJavaValidationProbe
    {
        public IReadOnlyDictionary<string, string?>? LastEnvironment { get; private set; }

        public async Task<JavaProbeResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string?> environment,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            LastEnvironment = environment;
            var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = commandInterpreter,
                    WorkingDirectory = Path.GetDirectoryName(executable)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("/d");
            process.StartInfo.ArgumentList.Add("/c");
            process.StartInfo.ArgumentList.Add(executable);
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

            Assert.IsTrue(process.Start());
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new TimeoutException();
            }

            var output = await stdout + await stderr;
            return new JavaProbeResult(process.ExitCode, Encoding.Default.GetBytes(output));
        }
    }
}
