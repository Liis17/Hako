using System.Diagnostics;
using Hako.Core;
using Hako.Core.Instances;

namespace Hako.Tests;

[TestClass]
public sealed class InstanceStorageUsageTests
{
    private const int BlockSize = 32768;

    [TestMethod]
    public void SplitsInstanceFilesIntoCategoriesAndSeparatesBackupTypes()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Paths.GameRoot;
        Directory.CreateDirectory(root);
        Write(root, "A/java/bin/java", 1);
        Write(root, "A/minecraft/assets/index.json", 2);
        Write(root, "A/minecraft/mods/mod.jar", 3);
        Write(root, "A/minecraft/resourcepacks/pack.zip", 4);
        Write(root, "A/minecraft/.hako-disabled-resourcepacks/old.zip", 5);
        Write(root, "A/minecraft/screenshots/shot.png", 6);
        Write(root, "A/minecraft/saves/W/level.dat", 7);
        Write(root, "A/minecraft/saves/W/region/r.0.0.mca", 8);
        Write(root, "A/minecraft/saves/W/datapacks/pack.zip", 9);
        Write(root, "A/minecraft/saves/W/.hako-disabled-datapacks/off.zip", 10);
        Write(root, "A/icon.png", 11);
        Write(root, "backups/A.hakobackup", 12);
        Write(root, "worlds/W.hakoworld", 13);
        Write(root, "instances.json", 14);

        var usage = new InstanceStorage(temporary.Paths).Usage();

        Assert.AreEqual(1L * BlockSize, usage[InstanceStorageUsageCategory.Java]);
        Assert.AreEqual((2L + 11 + 14) * BlockSize, usage[InstanceStorageUsageCategory.Minecraft]);
        Assert.AreEqual(3L * BlockSize, usage[InstanceStorageUsageCategory.Mods]);
        Assert.AreEqual((4L + 5) * BlockSize, usage[InstanceStorageUsageCategory.ResourcePacks]);
        Assert.AreEqual(6L * BlockSize, usage[InstanceStorageUsageCategory.Screenshots]);
        Assert.AreEqual((7L + 8) * BlockSize, usage[InstanceStorageUsageCategory.Worlds]);
        Assert.AreEqual((9L + 10) * BlockSize, usage[InstanceStorageUsageCategory.Datapacks]);
        Assert.AreEqual(12L * BlockSize, usage[InstanceStorageUsageCategory.BuildBackups]);
        Assert.AreEqual(13L * BlockSize, usage[InstanceStorageUsageCategory.WorldBackups]);
        Assert.AreEqual(105L * BlockSize, usage.Total);
    }

    [TestMethod]
    public void CountsGameRootJunctionTargetButSkipsNestedJunctions()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Paths.GameRoot;
        var target = Path.Combine(temporary.Root, "outside", "game");
        var nestedTarget = Path.Combine(temporary.Root, "outside", "linked");
        var nestedJunction = Path.Combine(target, "Build", "Minecraft", "Mods", "link");
        Write(target, "Build/Minecraft/Mods/local.jar", 2);
        Directory.CreateDirectory(Path.GetDirectoryName(nestedJunction)!);
        Directory.CreateDirectory(nestedTarget);
        File.WriteAllBytes(Path.Combine(nestedTarget, "external.jar"), new byte[3 * BlockSize]);
        using var nestedCleanup = CreateJunction(temporary.Root, nestedJunction, nestedTarget);
        using var rootCleanup = CreateJunction(temporary.Root, root, target);
        var paths = HakoPaths.CreateIsolated(root, Path.Combine(temporary.Root, "JunctionData"));

        var usage = new InstanceStorage(paths).Usage();

        Assert.AreEqual(2L * BlockSize, usage.Total);
        Assert.AreEqual(2L * BlockSize, usage[InstanceStorageUsageCategory.Mods]);
    }

    [TestMethod]
    public void MissingRootHasNoUsage()
    {
        using var temporary = new TemporaryDirectory();

        var usage = new InstanceStorage(temporary.Paths).Usage();

        Assert.AreEqual(0L, usage.Total);
    }

    [TestMethod]
    public void FolderClassificationIgnoresCase()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Paths.GameRoot;
        Write(root, "Build/Minecraft/Mods/mod.jar", 2);
        Write(root, "Build/Minecraft/SCREENSHOTS/shot.png", 3);

        var usage = new InstanceStorage(temporary.Paths).Usage();

        Assert.AreEqual(2L * BlockSize, usage[InstanceStorageUsageCategory.Mods]);
        Assert.AreEqual(3L * BlockSize, usage[InstanceStorageUsageCategory.Screenshots]);
    }

    private static void Write(string root, string relativePath, int blocks)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[blocks * BlockSize]);
    }

    private static JunctionCleanup CreateJunction(string root, string junction, string target)
    {
        var command = "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path '"
            + junction.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command }
        }) ?? throw new InvalidOperationException("Could not start PowerShell to create the test junction.");
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
        Assert.IsTrue((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
        return new JunctionCleanup(root, junction, target);
    }

    private sealed class JunctionCleanup(string root, string junction, string target) : IDisposable
    {
        public void Dispose()
        {
            var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            Assert.IsTrue(Path.GetFullPath(junction).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(Path.GetFullPath(target).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase));
            if ((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0) Directory.Delete(junction);
            Assert.IsTrue(Directory.Exists(target));
        }
    }
}
