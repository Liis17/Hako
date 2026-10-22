using System.Text.Json;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
public sealed class MinecraftLaunchPlanTests
{
    [TestMethod]
    public void OfficialFixturesBuildForThePinnedWindowsArchitecture()
    {
        foreach (var version in new[] { "1.6.4", "1.19", "22w18a" })
        {
            foreach (var architecture in new[] { GameArchitecture.X64, GameArchitecture.Arm64 })
            {
                using var temporary = new TemporaryDirectory();
                var root = Path.Combine(temporary.Root, version);
                Directory.CreateDirectory(root);
                var manifest = ReadManifest(version);
                var plan = MinecraftLaunchPlan.Build(manifest, root, "javaw.exe", MinecraftLaunchIdentity.Offline("Player"),
                    LaunchArgumentSource.Mojang, new InstanceParameters { MaximumMemoryMiB = 2048 }, architecture,
                    libraries: [], javaMajorVersion: 17);

                Assert.AreEqual(Path.Combine(root, "minecraft"), plan.WorkingDirectory);
                Assert.AreEqual("javaw.exe", plan.Executable);
                Assert.IsTrue(plan.Arguments.Contains(manifest.MainClass));
                Assert.IsTrue(plan.Arguments.Contains("Player"));
                var manifestArguments = manifest.MinecraftArguments
                    ?? string.Join(' ', manifest.Arguments!.Values.SelectMany(arguments => arguments).SelectMany(argument => argument.Values));
                if (manifestArguments.Contains("${auth_uuid}", StringComparison.Ordinal))
                {
                    Assert.IsTrue(plan.Arguments.Contains("a01e3843e5213998958af459800e4d11"));
                }

                if (manifestArguments.Contains("${auth_session}", StringComparison.Ordinal))
                {
                    Assert.IsTrue(plan.Arguments.Contains("0"));
                }

                if (manifestArguments.Contains("${user_type}", StringComparison.Ordinal))
                {
                    Assert.IsTrue(plan.Arguments.Contains("legacy"));
                }
                Assert.IsFalse(plan.Arguments.Any(argument => argument.Contains("${", StringComparison.Ordinal)));
                Assert.IsFalse(plan.Arguments.Contains("-XstartOnFirstThread"));
            }
        }
    }

    [TestMethod]
    public void ClasspathKeepsManifestLibraryOrderAndPutsClientLast()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Root, "Build");
        var manifest = ReadManifest("1.19");
        var libraries = new[]
        {
            new LibraryInstallation(new MojangDownload(), "org/example/first/1.0/first-1.0.jar"),
            new LibraryInstallation(new MojangDownload(), "org/example/second/2.0/second-2.0.jar"),
            new LibraryInstallation(new MojangDownload(), "org/example/native/3.0/native-3.0.jar", IsNative: true)
        };

        var plan = MinecraftLaunchPlan.Build(manifest, root, "javaw.exe", MinecraftLaunchIdentity.Offline("Player"),
            LaunchArgumentSource.Mojang, new InstanceParameters { MaximumMemoryMiB = 2048 }, GameArchitecture.X64,
            libraries: libraries, javaMajorVersion: 17);
        var classpathFlag = Array.IndexOf(plan.Arguments.ToArray(), "-cp");
        var classpath = plan.Arguments[classpathFlag + 1].Split(';');

        CollectionAssert.AreEqual(new[]
        {
            Path.Combine(root, "minecraft", "libraries", "org", "example", "first", "1.0", "first-1.0.jar"),
            Path.Combine(root, "minecraft", "libraries", "org", "example", "second", "2.0", "second-2.0.jar"),
            Path.Combine(root, "minecraft", "versions", "1.19", "1.19.jar")
        }, classpath);
    }

    [TestMethod]
    public void WindowAndFullscreenOptionsUseSelectedDimensions()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Root, "Build");
        var manifest = ReadManifest("1.19");
        var windowed = MinecraftLaunchPlan.Build(manifest, root, "javaw.exe", MinecraftLaunchIdentity.Offline("Player"),
            LaunchArgumentSource.Mojang, new InstanceParameters { WindowWidth = 1600, WindowHeight = 900, MaximumMemoryMiB = 2048 },
            GameArchitecture.X64, libraries: [], javaMajorVersion: 17);
        var fullscreen = MinecraftLaunchPlan.Build(manifest, root, "javaw.exe", MinecraftLaunchIdentity.Offline("Player"),
            LaunchArgumentSource.Mojang, new InstanceParameters { Fullscreen = true, MaximumMemoryMiB = 2048 },
            GameArchitecture.X64, libraries: [], javaMajorVersion: 17);

        var width = Array.IndexOf(windowed.Arguments.ToArray(), "--width");
        var height = Array.IndexOf(windowed.Arguments.ToArray(), "--height");
        Assert.AreEqual("1600", windowed.Arguments[width + 1]);
        Assert.AreEqual("900", windowed.Arguments[height + 1]);
        Assert.IsTrue(fullscreen.Arguments.Contains("--fullscreen"));
        Assert.IsFalse(fullscreen.Arguments.Contains("--width"));
    }

    [TestMethod]
    public void VirtualAndMappedAssetsUseTheMinecraftAssetDirectories()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Root, "Build");
        var manifest = new MojangVersionMetadata
        {
            Id = "1.19",
            MainClass = "net.minecraft.client.main.Main",
            Assets = "1.19",
            MinecraftArguments = "--assetsDir ${game_assets}"
        };
        var virtualAssets = MinecraftLaunchPlan.Build(manifest, root, "javaw.exe", MinecraftLaunchIdentity.Offline("Player"),
            LaunchArgumentSource.Mojang, new InstanceParameters { MaximumMemoryMiB = 2048 }, GameArchitecture.X64,
            new MojangAssetIndex { Virtual = true }, [], javaMajorVersion: 17);
        var mappedAssets = MinecraftLaunchPlan.Build(manifest, root, "javaw.exe", MinecraftLaunchIdentity.Offline("Player"),
            LaunchArgumentSource.Mojang, new InstanceParameters { MaximumMemoryMiB = 2048 }, GameArchitecture.X64,
            new MojangAssetIndex { MapToResources = true }, [], javaMajorVersion: 17);

        Assert.IsTrue(virtualAssets.Arguments.Contains(Path.Combine(root, "minecraft", "assets", "virtual", "1.19")));
        Assert.IsTrue(mappedAssets.Arguments.Contains(Path.Combine(root, "minecraft", "resources")));
    }

    [TestMethod]
    public void CustomArgumentsCannotReplaceLauncherOwnedGameFlags()
    {
        using var temporary = new TemporaryDirectory();
        var error = Assert.ThrowsExactly<MinecraftLaunchException>(() => MinecraftLaunchPlan.ArgumentTemplates(
            ReadManifest("1.6.4"), LaunchArgumentSource.Custom,
            new InstanceParameters { MinecraftArguments = "--username somebody-else" }, GameArchitecture.X64));

        Assert.AreEqual(MinecraftLaunchError.ReservedGameArgument, error.Error);
        Assert.AreEqual("LaunchReservedGameArgument", error.ResourceKey);
    }

    [TestMethod]
    public void OfflineUuidMatchesJavaNameUuidAndIsCaseSensitive()
    {
        Assert.AreEqual("b50ad385829d3141a2167e7d7539ba7f", MinecraftLaunchIdentity.Offline("Notch").Uuid);
        Assert.AreEqual("a01e3843e5213998958af459800e4d11", MinecraftLaunchIdentity.Offline("Player").Uuid);
        Assert.AreNotEqual(MinecraftLaunchIdentity.Offline("Player").Uuid, MinecraftLaunchIdentity.Offline("player").Uuid);
        Assert.AreEqual("0", MinecraftLaunchIdentity.Offline("Player").AccessToken);
        Assert.AreEqual("legacy", MinecraftLaunchIdentity.Offline("Player").UserType);
        Assert.IsFalse(MinecraftLaunchIdentity.Offline("Player").ToString().Contains("access-token", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NativeClassifierReplacesArchUsingThePinnedBuildArchitecture()
    {
        var manifest = JsonSerializer.Deserialize<MojangVersionMetadata>("""
            {"id":"fixture","libraries":[
              {"name":"org.lwjgl:lwjgl:3.0","natives":{"windows":"natives-windows-${arch}"},
               "downloads":{"artifact":{"path":"org/lwjgl/lwjgl/3.0/lwjgl.jar"},
                            "classifiers":{"natives-windows-64":{"path":"org/lwjgl/lwjgl/3.0/native-64.jar"},
                                           "natives-windows-arm64":{"path":"org/lwjgl/lwjgl/3.0/native-arm64.jar"}}},
               "extract":{"exclude":[]}}
            ]}
            """, MojangJsonContext.Default.MojangVersionMetadata)!;

        var x64 = MojangCompatibility.Libraries(manifest, GameArchitecture.X64);
        var arm64 = MojangCompatibility.Libraries(manifest, GameArchitecture.Arm64);

        Assert.AreEqual("org/lwjgl/lwjgl/3.0/native-64.jar", x64.Single(library => library.IsNative).Path);
        Assert.AreEqual("org/lwjgl/lwjgl/3.0/native-arm64.jar", arm64.Single(library => library.IsNative).Path);
    }

    private static MojangVersionMetadata ReadManifest(string version)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mojang", version + ".json");
        return JsonSerializer.Deserialize(File.ReadAllBytes(path), MojangJsonContext.Default.MojangVersionMetadata)
            ?? throw new AssertFailedException($"Fixture {version} is empty.");
    }
}
