using System.Text.Json;
using Hako.Core;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
public sealed class GameProcessRunnerTests
{
    [TestMethod]
    public async Task RunnerWritesLogAndSwiftCompatibleProcessRecordThenRemovesItOnExit()
    {
        using var temporary = new TemporaryDirectory();
        var instanceRoot = CreateInstanceRoot(temporary);
        var sessionId = Guid.NewGuid();
        var runner = new GameProcessRunner();
        var launch = await runner.StartAsync(CreatePowerShellPlan(instanceRoot,
                "Start-Sleep -Seconds 2; [Console]::WriteLine('Hako runner log'); exit 7"),
            Guid.NewGuid(), instanceRoot, sessionId);
        var recordPath = Path.Combine(instanceRoot, "minecraft", ".hako-running.json");

        Assert.IsTrue(File.Exists(recordPath));
        Assert.IsTrue(File.Exists(launch.LogPath));
        Assert.IsTrue(launch.Record.Pid > 0);
        Assert.IsTrue(launch.Record.StartSeconds > 0);
        Assert.IsTrue(launch.Record.StartMicroseconds < 1_000_000);
        using var json = JsonDocument.Parse(File.ReadAllBytes(recordPath));
        Assert.AreEqual(launch.Record.InstanceID, json.RootElement.GetProperty("instanceID").GetGuid());
        Assert.AreEqual(launch.Record.Pid, json.RootElement.GetProperty("pid").GetInt32());
        Assert.AreEqual(launch.Record.StartSeconds, json.RootElement.GetProperty("startSeconds").GetUInt64());
        Assert.AreEqual(launch.Record.StartMicroseconds, json.RootElement.GetProperty("startMicroseconds").GetUInt64());
        Assert.AreEqual(sessionId, json.RootElement.GetProperty("sessionID").GetGuid());
        Assert.AreEqual(7, await launch.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        using var log = new FileStream(launch.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(log);
        StringAssert.Contains(reader.ReadToEnd(), "Hako runner log");
        Assert.IsFalse(File.Exists(recordPath));
    }

    [TestMethod]
    public async Task RestoreAttachesToLiveProcessAndWaitsForItsExit()
    {
        using var temporary = new TemporaryDirectory();
        var instanceRoot = CreateInstanceRoot(temporary);
        var instanceId = Guid.NewGuid();
        var launched = await new GameProcessRunner().StartAsync(CreatePowerShellPlan(instanceRoot, "Start-Sleep -Seconds 3; exit 0"), instanceId, instanceRoot);

        var restored = await new GameProcessRunner().RestoreAsync(instanceId, instanceRoot);

        Assert.IsNotNull(restored);
        Assert.AreEqual(launched.Record, restored!.Record);
        Assert.AreEqual(0, await restored.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(0, await launched.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsFalse(File.Exists(Path.Combine(instanceRoot, "minecraft", ".hako-running.json")));
    }

    [TestMethod]
    public async Task ExitCompletesWhenRunningRecordIsTemporarilyLocked()
    {
        using var temporary = new TemporaryDirectory();
        var instanceRoot = CreateInstanceRoot(temporary);
        var instanceId = Guid.NewGuid();
        var launch = await new GameProcessRunner().StartAsync(
            CreatePowerShellPlan(instanceRoot, "Start-Sleep -Seconds 3; exit 0"), instanceId, instanceRoot);
        var recordPath = Path.Combine(instanceRoot, "minecraft", ".hako-running.json");

        using (new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.AreEqual(0, await launch.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsTrue(File.Exists(recordPath));
        }

        Assert.IsNull(await new GameProcessRunner().RestoreAsync(instanceId, instanceRoot));
        Assert.IsFalse(File.Exists(recordPath));
    }

    [TestMethod]
    public async Task RestoreRejectsReusedPidWhenInjectedCreationTimeDoesNotMatch()
    {
        using var temporary = new TemporaryDirectory();
        var instanceRoot = CreateInstanceRoot(temporary);
        var instanceId = Guid.NewGuid();
        var launched = await new GameProcessRunner().StartAsync(CreatePowerShellPlan(instanceRoot, "Start-Sleep -Seconds 3; exit 0"), instanceId, instanceRoot);
        var reusedPidRecord = launched.Record with { StartSeconds = launched.Record.StartSeconds + 1 };
        WriteRecord(instanceRoot, reusedPidRecord);
        var runner = new GameProcessRunner(_ => (true, launched.Record.StartSeconds, launched.Record.StartMicroseconds));

        var restored = await runner.RestoreAsync(instanceId, instanceRoot);

        Assert.IsNull(restored);
        Assert.IsFalse(File.Exists(Path.Combine(instanceRoot, "minecraft", ".hako-running.json")));
        Assert.AreEqual(0, await launched.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public async Task OldProcessObserverDoesNotDeleteAReplacedRunningRecord()
    {
        using var temporary = new TemporaryDirectory();
        var instanceRoot = CreateInstanceRoot(temporary);
        var instanceId = Guid.NewGuid();
        var launched = await new GameProcessRunner().StartAsync(CreatePowerShellPlan(instanceRoot, "Start-Sleep -Seconds 3; exit 0"), instanceId, instanceRoot);
        var replacement = launched.Record with { SessionID = Guid.NewGuid() };
        WriteRecord(instanceRoot, replacement);

        Assert.AreEqual(0, await launched.Completion.WaitAsync(TimeSpan.FromSeconds(10)));

        var current = JsonSerializer.Deserialize(
            File.ReadAllBytes(Path.Combine(instanceRoot, "minecraft", ".hako-running.json")),
            HakoJsonContext.Default.GameProcessRecord);
        Assert.AreEqual(replacement, current);
    }

    [TestMethod]
    public void Java8CommandLineOverWindowsLimitHasStructuredError()
    {
        var error = Assert.ThrowsExactly<MinecraftLaunchException>(() =>
            Win32Process.BuildCommandLine("java.exe", [new string('a', Win32Process.MaximumCommandLineCharacters)]));

        Assert.AreEqual(MinecraftLaunchError.CommandLineTooLong, error.Error);
        Assert.AreEqual("LaunchCommandLineTooLong", error.ResourceKey);
    }

    private static string CreateInstanceRoot(TemporaryDirectory temporary)
    {
        var root = Path.Combine(temporary.Paths.GameRoot, "Build");
        Directory.CreateDirectory(Path.Combine(root, "minecraft"));
        return root;
    }

    private static MinecraftLaunchPlan CreateCommandPlan(string instanceRoot, string command) => new(
        Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"),
        ["/d", "/c", command],
        Path.Combine(instanceRoot, "minecraft"),
        8);

    private static MinecraftLaunchPlan CreatePowerShellPlan(string instanceRoot, string command) => new(
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
        Path.Combine(instanceRoot, "minecraft"),
        8);

    private static void WriteRecord(string instanceRoot, GameProcessRecord record)
    {
        File.WriteAllBytes(
            Path.Combine(instanceRoot, "minecraft", ".hako-running.json"),
            JsonSerializer.SerializeToUtf8Bytes(record, HakoJsonContext.Default.GameProcessRecord));
    }
}
