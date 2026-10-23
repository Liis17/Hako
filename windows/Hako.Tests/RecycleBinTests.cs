using Hako.Core;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Hako.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class RecycleBinTests
{
    [TestMethod]
    public void ProgressSinkCancelsAnyDeleteNotMarkedForRecycle()
    {
        var sink = new RecycleBinProgressSink();

        Assert.AreEqual(unchecked((int)0x80004004), RecycleBinProgressSink.HResultAbort);
        Assert.AreEqual(RecycleBinProgressSink.HResultAbort, sink.PreDeleteItem(0, null!));
        Assert.AreEqual(RecycleBinProgressSink.HResultAbort, sink.PreDeleteItem(0x2, null!));
        Assert.IsFalse(sink.RecycleWasRequested);
        Assert.IsTrue(sink.NotRecyclable);
        Assert.AreEqual(0, sink.PreDeleteItem(RecycleBinProgressSink.TransferDeleteRecycleIfPossible, null!));
        Assert.IsTrue(sink.RecycleWasRequested);

        sink.PostDeleteItem(0, null!, unchecked((int)0x80004005), null);
        Assert.IsFalse(sink.DeleteSucceeded);
        sink.PostDeleteItem(0, null!, 0, null);
        Assert.IsFalse(sink.DeleteSucceeded);
    }

    [TestMethod]
    public async Task UnavailableRecycleBinLeavesSourceFileInPlace()
    {
        using var temporary = new TemporaryDirectory();
        var file = Path.Combine(temporary.Path, "source.txt");
        await File.WriteAllTextAsync(file, "keep me");
        var recycleBin = new RecycleBin(new UnavailableOperation());

        var result = await recycleBin.DeleteAsync(file);

        Assert.AreEqual(RecycleBinItemStatus.Failed, result.Items.Single().Status);
        Assert.AreEqual("No recycle bin is available.", result.Items.Single().Error);
        Assert.IsTrue(File.Exists(file));
        Assert.AreEqual("keep me", await File.ReadAllTextAsync(file));
    }

    [TestMethod]
    public async Task NotRecyclableItemIsReturnedWithItsReasonAndRemainsInPlace()
    {
        using var temporary = new TemporaryDirectory();
        var file = Path.Combine(temporary.Path, "not-recyclable.txt");
        await File.WriteAllTextAsync(file, "keep me");
        var recycleBin = new RecycleBin(new NotRecyclableOperation());

        var result = await recycleBin.DeleteAsync([file]);

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(Path.GetFullPath(file), result.Items[0].Path);
        Assert.AreEqual(RecycleBinItemStatus.NotRecyclable, result.Items[0].Status);
        Assert.IsTrue(File.Exists(file));
        Assert.AreEqual("keep me", await File.ReadAllTextAsync(file));
    }

    [TestMethod]
    public async Task MixedBatchReturnsStatusAndErrorForEveryItemAfterPartialFailure()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new[] { "recycled.txt", "failed.txt", "cancelled.txt" }
            .Select(name => Path.Combine(temporary.Path, name)).ToArray();
        var recycleBin = new RecycleBin(new MixedOperation());

        var result = await recycleBin.DeleteAsync(paths);

        Assert.AreEqual(3, result.Items.Count);
        Assert.AreEqual(RecycleBinItemStatus.Recycled, result.Items[0].Status);
        Assert.AreEqual(RecycleBinItemStatus.Failed, result.Items[1].Status);
        Assert.AreEqual("Injected item failure.", result.Items[1].Error);
        Assert.AreEqual(RecycleBinItemStatus.Cancelled, result.Items[2].Status);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task FileIsMovedToTheWindowsRecycleBin()
    {
        RequireRecycleBinIntegration();
        using var temporary = new TemporaryDirectory();
        var file = Path.Combine(temporary.Path, "trash-me.txt");
        await File.WriteAllTextAsync(file, "reversible");

        var result = await new RecycleBin().DeleteAsync(file);
        Assert.AreEqual(RecycleBinItemStatus.Recycled, result.Items.Single().Status, result.Items.Single().Error);

        Assert.IsFalse(File.Exists(file));
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task LongPathIsEitherRecycledOrReportedAsNotRecyclableWithoutDataLoss()
    {
        RequireRecycleBinIntegration();
        using var temporary = new TemporaryDirectory();
        var directory = temporary.Path;
        try
        {
            for (var index = 0; index < 4; index++)
            {
                directory = Path.Combine(directory, new string((char)('a' + index), 80));
                Directory.CreateDirectory(directory);
            }

            var file = Path.Combine(directory, "long-path.txt");
            await File.WriteAllTextAsync(file, "reversible");
            Assert.IsTrue(file.Length > 260);
            var result = await new RecycleBin().DeleteAsync(file);
            var item = result.Items.Single();
            if (item.Status == RecycleBinItemStatus.NotRecyclable)
            {
                Assert.IsTrue(File.Exists(file));
                Assert.AreEqual("reversible", await File.ReadAllTextAsync(file));
            }
            else if (item.Status == RecycleBinItemStatus.Recycled)
            {
                Assert.IsFalse(File.Exists(file));
            }
            else
            {
                Assert.Fail($"The Shell could not confirm the long-path result ({item.Status}): {item.Error}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("This environment does not allow creating long-path test files: " + exception.Message);
        }
    }

    [TestMethod]
    public async Task PermanentDeleteRemovesAFileBeyondTheLegacyPathLimit()
    {
        using var temporary = new TemporaryDirectory();
        var directory = temporary.Path;
        try
        {
            for (var index = 0; index < 4; index++)
            {
                directory = Path.Combine(directory, new string((char)('d' + index), 80));
                Directory.CreateDirectory(directory);
            }

            var file = Path.Combine(directory, "permanent-delete.txt");
            await File.WriteAllTextAsync(file, "remove");
            Assert.IsTrue(file.Length > 260);

            new RecycleBin().PermanentDelete(file, temporary.Path);

            Assert.IsFalse(File.Exists(file));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("This environment does not allow creating long-path test files: " + exception.Message);
        }
    }

    [TestMethod]
    public async Task PermanentDeleteRemovesNestedJunctionWithoutTouchingItsTarget()
    {
        using var temporary = new TemporaryDirectory();
        var folder = Path.Combine(temporary.Path, "to-delete");
        var target = Path.Combine(temporary.Path, "external-target");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(target);
        var targetFile = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(targetFile, "keep");
        var junction = Path.Combine(folder, "outside");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", junction, target }
        });
        process!.WaitForExit();
        if (process.ExitCode != 0)
        {
            Assert.Inconclusive("Could not create a junction in the temporary test folder: " + process.StandardError.ReadToEnd());
        }

        new RecycleBin().PermanentDelete(folder, temporary.Path);

        Assert.IsFalse(Directory.Exists(folder));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(targetFile));
    }

    [TestMethod]
    public async Task PermanentDeleteRejectsRelativeVolumeRootOutsideAndReparseTraversalPaths()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Path;
        var file = Path.Combine(root, "keep.txt");
        await File.WriteAllTextAsync(file, "keep");
        var recycleBin = new RecycleBin(new NotRecyclableOperation());

        Assert.ThrowsExactly<ArgumentException>(() => recycleBin.PermanentDelete("relative.txt", root));
        Assert.ThrowsExactly<ArgumentException>(() => recycleBin.PermanentDelete(file, Path.GetPathRoot(root)!));
        Assert.ThrowsExactly<ArgumentException>(() => recycleBin.PermanentDelete(Path.GetPathRoot(root)!, root));
        Assert.ThrowsExactly<ArgumentException>(() => recycleBin.PermanentDelete(Path.Combine(Path.GetDirectoryName(root)!, "outside.txt"), root));

        var outsideTarget = Path.Combine(temporary.Path, "outside-target");
        Directory.CreateDirectory(outsideTarget);
        var outsideFile = Path.Combine(outsideTarget, "target.txt");
        await File.WriteAllTextAsync(outsideFile, "keep target");
        var junction = Path.Combine(root, "outside-link");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", junction, outsideTarget }
        });
        process!.WaitForExit();
        if (process.ExitCode != 0)
        {
            Assert.Inconclusive("Could not create a junction in the temporary test folder: " + process.StandardError.ReadToEnd());
        }

        Assert.ThrowsExactly<ArgumentException>(() => recycleBin.PermanentDelete(Path.Combine(junction, "target.txt"), root));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(file));
        Assert.AreEqual("keep target", await File.ReadAllTextAsync(outsideFile));
        Directory.Delete(junction);

        var rootTarget = Path.Combine(root, "root-target");
        Directory.CreateDirectory(rootTarget);
        var rootTargetFile = Path.Combine(rootTarget, "keep.txt");
        await File.WriteAllTextAsync(rootTargetFile, "keep root target");
        var rootJunction = Path.Combine(root, "root-link");
        using var rootProcess = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", rootJunction, rootTarget }
        });
        rootProcess!.WaitForExit();
        if (rootProcess.ExitCode != 0)
        {
            Assert.Inconclusive("Could not create a root junction in the temporary test folder: " + rootProcess.StandardError.ReadToEnd());
        }

        Assert.ThrowsExactly<ArgumentException>(() => recycleBin.PermanentDelete(Path.Combine(rootJunction, "keep.txt"), rootJunction));
        Assert.AreEqual("keep root target", await File.ReadAllTextAsync(rootTargetFile));
        Directory.Delete(rootJunction);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task RepeatedRecycleOperationsReleaseComWrappersAcrossStaThreads()
    {
        RequireRecycleBinIntegration();
        using var temporary = new TemporaryDirectory();
        var recycleBin = new RecycleBin();
        for (var index = 0; index < 8; index++)
        {
            var file = Path.Combine(temporary.Path, $"release-{index}.txt");
            await File.WriteAllTextAsync(file, "reversible");

            var item = (await recycleBin.DeleteAsync(file)).Items.Single();
            Assert.IsTrue(item.Status is RecycleBinItemStatus.Recycled or RecycleBinItemStatus.NotRecyclable,
                $"Unexpected Shell result: {item.Status} ({item.Error})");
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class UnavailableOperation : IRecycleBinOperation
    {
        public Task<RecycleBinItemStatus> DeleteAsync(string path, CancellationToken cancellationToken) =>
            Task.FromException<RecycleBinItemStatus>(new RecycleBinException("No recycle bin is available."));
    }

    private sealed class NotRecyclableOperation : IRecycleBinOperation
    {
        public Task<RecycleBinItemStatus> DeleteAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(RecycleBinItemStatus.NotRecyclable);
    }

    private sealed class MixedOperation : IRecycleBinOperation
    {
        public Task<RecycleBinItemStatus> DeleteAsync(string path, CancellationToken cancellationToken) =>
            Path.GetFileName(path) switch
            {
                "recycled.txt" => Task.FromResult(RecycleBinItemStatus.Recycled),
                "failed.txt" => Task.FromException<RecycleBinItemStatus>(new IOException("Injected item failure.")),
                _ => Task.FromException<RecycleBinItemStatus>(new OperationCanceledException("Injected cancellation."))
            };
    }

    private static void RequireRecycleBinIntegration()
    {
        if (Environment.GetEnvironmentVariable("HAKO_RUN_RECYCLE_BIN_TESTS") != "1")
        {
            Assert.Inconclusive("Set HAKO_RUN_RECYCLE_BIN_TESTS=1 to move test files to the real Windows Recycle Bin.");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Hako.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
