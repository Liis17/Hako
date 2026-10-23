using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class MinecraftInstallationTests
{
    [TestMethod]
    [DataRow("1.6.4.json", 8, "X64", "Arm64", "jre-legacy", false)]
    [DataRow("1.6.4.json", 8, "X86", "X86", "jre-legacy", false)]
    [DataRow("1.19.json", 17, "Arm64", "Arm64", "java-runtime-gamma", true)]
    [DataRow("22w18a.json", 17, "X64", "Arm64", "java-runtime-gamma", true)]
    public async Task MojangFixturesPrepareLegacyModernAndSnapshotMetadata(
        string fixtureName,
        int javaMajor,
        string architectureName,
        string hostMachineName,
        string javaComponent,
        bool hasModernArguments)
    {
        using var temporary = new TestDirectory();
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mojang", fixtureName);
        var manifestBytes = await File.ReadAllBytesAsync(fixturePath);
        using var document = JsonDocument.Parse(manifestBytes);
        var versionId = document.RootElement.GetProperty("id").GetString()!;
        var runtimeManifest = Encoding.UTF8.GetBytes("{\"files\":{\"bin/javaw.exe\":{\"type\":\"file\",\"downloads\":{\"raw\":{\"url\":\"https://test/javaw.exe\",\"sha1\":\"" + Hash("java") + "\",\"size\":4}}}}}");
        var runtimeIndexBytes = RewriteRuntimeFixture(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mojang", "java-runtimes.json")), runtimeManifest);
        var versionUrl = $"https://test/{versionId}.json";
        var handler = new PayloadHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [versionUrl] = manifestBytes,
            [MojangClient.RuntimeIndexUrl] = runtimeIndexBytes,
            ["https://test/runtime-manifest.json"] = runtimeManifest
        });
        using var client = new MojangClient(new HttpClient(handler));
        var hostMachine = Enum.Parse<HostMachineArchitecture>(hostMachineName);
        var prepared = await client.PrepareAsync(new MojangVersionEntry
        {
            Id = versionId,
            Type = "release",
            Url = versionUrl,
            Sha1 = Hash(manifestBytes)
        }, new HostArchitecture(hostMachine, Amd64UserEnabled: hostMachine == HostMachineArchitecture.Arm64));

        Assert.AreEqual(versionId, prepared.Manifest.Id);
        Assert.AreEqual(javaMajor, prepared.Manifest.Java.MajorVersion);
        Assert.AreEqual(javaMajor, prepared.Runtime.MajorVersion);
        Assert.AreEqual(architectureName, prepared.Architecture.ToString());
        Assert.IsTrue(prepared.Libraries.Count > 0);
        Assert.AreEqual(javaComponent, prepared.Manifest.Java.Component);
        Assert.AreEqual(hasModernArguments, prepared.Manifest.Arguments is not null);
        Assert.AreEqual(!hasModernArguments, !string.IsNullOrWhiteSpace(prepared.Manifest.MinecraftArguments));
    }

    [TestMethod]
    public async Task CatalogAsyncReadsTheOfficialVersionManifestWithHakoUserAgent()
    {
        const string catalogJson = "{\"latest\":{\"release\":\"1.20.4\",\"snapshot\":\"24w01a\"},\"versions\":[{\"id\":\"1.20.4\",\"type\":\"release\",\"url\":\"https://test/version.json\",\"time\":\"2024-01-01T00:00:00+00:00\",\"releaseTime\":\"2024-01-01T00:00:00+00:00\",\"sha1\":\"0123456789012345678901234567890123456789\",\"complianceLevel\":1}]}";
        var handler = new PayloadHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [MojangClient.CatalogUrl] = Encoding.UTF8.GetBytes(catalogJson)
        });
        using var http = new HttpClient(handler);
        using var client = new MojangClient(http);

        var catalog = await client.CatalogAsync();

        Assert.AreEqual("1.20.4", catalog.Latest.Release);
        Assert.AreEqual("1.20.4", catalog.Versions.Single().Id);
        Assert.IsTrue(handler.UserAgents.Single().StartsWith("Hako/", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RulesMatchWindowsArchitectureVersionAndFeatures()
    {
        var rule = new MojangRule
        {
            Action = "allow",
            Os = new MojangOperatingSystem { Name = "windows", Arch = "^(amd64|x86_64)$" }
        };
        var featureRule = new MojangRule { Action = "allow", Features = new Dictionary<string, bool> { ["has_custom_resolution"] = false } };

        Assert.IsTrue(rule.Matches(GameArchitecture.X64));
        Assert.IsFalse(rule.Matches(GameArchitecture.Arm64));
        Assert.IsFalse(new MojangRule { Action = "allow", Os = new MojangOperatingSystem { Name = "osx" } }.Matches(GameArchitecture.X64));
        Assert.IsTrue(featureRule.Matches(GameArchitecture.X64));
        Assert.IsTrue(new MojangRule
        {
            Action = "allow",
            Os = new MojangOperatingSystem { Name = "windows", Version = "^" + Environment.OSVersion.Version.Major + "\\." }
        }.Matches(GameArchitecture.X64));
        Assert.IsFalse(new MojangRule
        {
            Action = "allow",
            Os = new MojangOperatingSystem { Arch = "^amd64$" }
        }.Matches(GameArchitecture.Arm64));
    }

    [TestMethod]
    [DataRow("1.19.json")]
    [DataRow("22w18a.json")]
    public async Task X86RuleFromMojangFixturesMatchesOnlyX86(string fixtureName)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mojang", fixtureName);
        var manifest = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(fixturePath), MojangJsonContext.Default.MojangVersionMetadata)!;
        var argument = manifest.Arguments!["jvm"].Single(value => value.Values.Contains("-Xss1M"));
        var rule = argument.Rules!.Single(value => value.Os?.Arch == "x86");

        Assert.IsTrue(rule.Matches(GameArchitecture.X86));
        Assert.IsFalse(rule.Matches(GameArchitecture.X64));
        Assert.IsFalse(rule.Matches(GameArchitecture.Arm64));
    }

    [TestMethod]
    public void LegacyNativeArchSubstitutionSelectsTheX86Classifier()
    {
        var manifest = new MojangVersionMetadata
        {
            Libraries =
            [
                new MojangLibrary
                {
                    Name = "org.test:legacy:1.0",
                    Natives = new Dictionary<string, string> { ["windows"] = "natives-windows-${arch}" },
                    Downloads = new MojangLibraryDownloads
                    {
                        Classifiers = new Dictionary<string, MojangDownload>
                        {
                            ["natives-windows-32"] = Download("legacy-32.jar")
                        }
                    }
                }
            ]
        };

        var libraries = MojangCompatibility.Libraries(manifest, GameArchitecture.X86);

        Assert.AreEqual("legacy-32.jar", libraries.Single().Path);
    }

    [TestMethod]
    public void LibrariesRetainManifestOrderWhenNativeLibrariesAreSelected()
    {
        var manifest = new MojangVersionMetadata
        {
            Libraries =
            [
                new MojangLibrary { Name = "org.test:first:1.0", Downloads = new MojangLibraryDownloads { Artifact = Download("first.jar") } },
                new MojangLibrary
                {
                    Name = "org.test:native:1.0",
                    Natives = new Dictionary<string, string> { ["windows"] = "natives-windows-${arch}" },
                    Downloads = new MojangLibraryDownloads
                    {
                        Classifiers = new Dictionary<string, MojangDownload>
                        {
                            ["natives-windows-64"] = Download("native.jar")
                        }
                    }
                },
                new MojangLibrary { Name = "org.test:last:1.0", Downloads = new MojangLibraryDownloads { Artifact = Download("last.jar") } }
            ]
        };

        var libraries = MojangCompatibility.Libraries(manifest, GameArchitecture.X64);

        CollectionAssert.AreEqual(new[] { "first.jar", "native.jar", "last.jar" }, libraries.Select(library => library.Path).ToArray());
    }

    [TestMethod]
    public void LegacyWindowsNativeWithoutSuffixIsSupportedOnX86()
    {
        var manifest = new MojangVersionMetadata
        {
            Libraries =
            [
                new MojangLibrary
                {
                    Name = "org.lwjgl:lwjgl:2.9.1:natives-windows",
                    Downloads = new MojangLibraryDownloads { Artifact = Download("lwjgl-windows.jar") }
                }
            ]
        };

        var libraries = MojangCompatibility.Libraries(manifest, GameArchitecture.X86);

        Assert.AreEqual("lwjgl-windows.jar", libraries.Single().Path);
        Assert.IsTrue(libraries.Single().IsNative);
    }

    [TestMethod]
    [DataRow("8u74-b01", 8)]
    [DataRow("1.8.0_202", 8)]
    [DataRow("17.0.15", 17)]
    [DataRow("25.0.1", 25)]
    public void JavaRuntimeVersionsUseMojangMajorVersionFormats(string name, int major)
    {
        var runtime = new MojangJavaRuntimePackage { Version = new MojangRuntimeVersion { Name = name } };

        Assert.AreEqual(major, runtime.MajorVersion);
    }

    [TestMethod]
    public async Task CompleteArm64RuntimeAndNativeSetIsPreferred()
    {
        var prepared = await PrepareSyntheticAsync(
            [("lwjgl", GameArchitecture.Arm64), ("lwjgl", GameArchitecture.X64)],
            [GameArchitecture.Arm64, GameArchitecture.X64],
            new HostArchitecture(HostMachineArchitecture.Arm64, true));

        Assert.AreEqual(GameArchitecture.Arm64, prepared.Architecture);
        Assert.AreEqual("windows-arm64", prepared.Architecture.MojangRuntimeKey());
        Assert.IsTrue(prepared.Libraries.Single(library => library.IsNative).Path.Contains("arm64", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MissingArm64JavaFallsBackAsACompleteX64Set()
    {
        var prepared = await PrepareSyntheticAsync(
            [("lwjgl", GameArchitecture.Arm64), ("lwjgl", GameArchitecture.X64)],
            [GameArchitecture.X64],
            new HostArchitecture(HostMachineArchitecture.Arm64, true));

        Assert.AreEqual(GameArchitecture.X64, prepared.Architecture);
        Assert.IsTrue(prepared.Libraries.Single(library => library.IsNative).Path.Contains("natives-windows", StringComparison.Ordinal));
        Assert.IsFalse(prepared.Libraries.Single(library => library.IsNative).Path.Contains("arm64", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MissingOneArm64NativeGroupFallsBackForTheWholeInstance()
    {
        var prepared = await PrepareSyntheticAsync(
            [("lwjgl", GameArchitecture.Arm64), ("lwjgl", GameArchitecture.X64), ("joml", GameArchitecture.X64)],
            [GameArchitecture.Arm64, GameArchitecture.X64],
            new HostArchitecture(HostMachineArchitecture.Arm64, true));

        Assert.AreEqual(GameArchitecture.X64, prepared.Architecture);
        Assert.AreEqual(2, prepared.Libraries.Count(library => library.IsNative));
        Assert.IsTrue(prepared.Libraries.Where(library => library.IsNative).All(library => !library.Path.Contains("arm64", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task IncompleteArm64AndX64SetsAreUnsupported()
    {
        var exception = await Assert.ThrowsExactlyAsync<MojangException>(() => PrepareSyntheticAsync(
            [("lwjgl", GameArchitecture.Arm64), ("joml", GameArchitecture.X64)],
            [GameArchitecture.Arm64, GameArchitecture.X64],
            new HostArchitecture(HostMachineArchitecture.Arm64, true)));

        Assert.AreEqual(MojangError.Unsupported, exception.Error);
    }

    [TestMethod]
    public async Task X64FallbackRequiresAmd64EmulationAndPinnedArchitectureIsRetained()
    {
        var unsupported = await Assert.ThrowsExactlyAsync<MojangException>(() => PrepareSyntheticAsync(
            [("lwjgl", GameArchitecture.X64)],
            [GameArchitecture.Arm64, GameArchitecture.X64],
            new HostArchitecture(HostMachineArchitecture.Arm64, false)));
        Assert.AreEqual(MojangError.Unsupported, unsupported.Error);

        var pinned = await PrepareSyntheticAsync(
            [("lwjgl", GameArchitecture.Arm64), ("lwjgl", GameArchitecture.X64)],
            [GameArchitecture.Arm64, GameArchitecture.X64],
            new HostArchitecture(HostMachineArchitecture.Arm64, true),
            GameArchitecture.X64);
        Assert.AreEqual(GameArchitecture.X64, pinned.Architecture);
    }

    [TestMethod]
    public async Task X64AndX86HostsUseOnlyTheirMatchingGameArchitecture()
    {
        var x64 = await PrepareSyntheticAsync(
            [("lwjgl", GameArchitecture.Arm64), ("lwjgl", GameArchitecture.X64)],
            [GameArchitecture.X64],
            new HostArchitecture(HostMachineArchitecture.X64, false));
        var x86 = await PrepareSyntheticAsync(
            [("lwjgl", GameArchitecture.X86)],
            [GameArchitecture.X86],
            new HostArchitecture(HostMachineArchitecture.X86, false));

        Assert.AreEqual(GameArchitecture.X64, x64.Architecture);
        Assert.AreEqual(GameArchitecture.X86, x86.Architecture);
        Assert.AreEqual("windows-x86", x86.Architecture.MojangRuntimeKey());
    }

    [TestMethod]
    public async Task MissingVersionFilesAreUnsupportedButHttpFailuresRemainHttpErrors()
    {
        const string versionUrl = "https://test/no-client.json";
        var noClient = Encoding.UTF8.GetBytes("{\"id\":\"no-client\",\"downloads\":{},\"libraries\":[]}");
        var handler = new PayloadHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal) { [versionUrl] = noClient });
        using var client = new MojangClient(new HttpClient(handler));
        var unsupported = await Assert.ThrowsExactlyAsync<MojangException>(() => client.PrepareAsync(
            new MojangVersionEntry { Id = "no-client", Url = versionUrl, Sha1 = Hash(noClient) },
            new HostArchitecture(HostMachineArchitecture.X64, false)));
        Assert.AreEqual(MojangError.Unsupported, unsupported.Error);

        var unavailable = await Assert.ThrowsExactlyAsync<MojangException>(() => client.CatalogAsync());
        Assert.AreEqual(MojangError.Http, unavailable.Error);
    }

    [TestMethod]
    public async Task InstallerWritesVerifiedFilesAndCopiesVirtualAndMappedAssets()
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(includeAssets: true);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        var result = await installer.InstallAsync(fixture.Prepared, temporary.Path);

        Assert.AreEqual(GameArchitecture.X64, result.Architecture);
        Assert.AreEqual("bin/javaw.exe", result.JavaExecutable);
        Assert.AreEqual("bin/javaw.exe", new GameInstance().JavaExecutable);
        Assert.AreEqual(17, result.JavaMajorVersion);
        Assert.AreEqual("javaw content", await File.ReadAllTextAsync(Path.Combine(temporary.Path, "java", "bin", "javaw.exe")));
        Assert.AreEqual("client content", await File.ReadAllTextAsync(Path.Combine(temporary.Path, "minecraft", "versions", "install-test", "install-test.jar")));
        Assert.AreEqual("library content", await File.ReadAllTextAsync(Path.Combine(temporary.Path, "minecraft", "libraries", "org", "test", "library.jar")));
        Assert.AreEqual("asset content", await File.ReadAllTextAsync(Path.Combine(temporary.Path, "minecraft", "assets", "objects", Hash("asset content")[..2], Hash("asset content"))));
        Assert.AreEqual("asset content", await File.ReadAllTextAsync(Path.Combine(temporary.Path, "minecraft", "assets", "virtual", "test-index", "lang", "en_us.json")));
        Assert.AreEqual("asset content", await File.ReadAllTextAsync(Path.Combine(temporary.Path, "minecraft", "resources", "lang", "en_us.json")));
        Assert.AreEqual("log config", await File.ReadAllTextAsync(Path.Combine(temporary.Path, "minecraft", "assets", "log_configs", "client.xml")));
        Assert.IsTrue(File.Exists(Path.Combine(temporary.Path, "minecraft", "versions", "install-test", "install-test.json")));
        Assert.IsTrue(File.Exists(Path.Combine(temporary.Path, "minecraft", "assets", "indexes", "test-index.json")));
        Assert.IsTrue(Directory.Exists(Path.Combine(temporary.Path, "minecraft", "mods")));
        Assert.IsTrue(Directory.Exists(Path.Combine(temporary.Path, "minecraft", "resourcepacks")));
    }

    [TestMethod]
    public async Task ValidFilesAreReusedAndCorruptFilesAreDownloadedAgain()
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(includeAssets: false);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);
        var clientPath = Path.Combine(temporary.Path, "minecraft", "versions", "install-test", "install-test.jar");

        await installer.InstallAsync(fixture.Prepared, temporary.Path);
        var firstCount = fixture.Handler.RequestCount("https://test/client.jar");
        await installer.InstallAsync(fixture.Prepared, temporary.Path);
        Assert.AreEqual(firstCount, fixture.Handler.RequestCount("https://test/client.jar"));

        await File.WriteAllTextAsync(clientPath, "corrupted");
        await installer.InstallAsync(fixture.Prepared, temporary.Path);

        Assert.AreEqual(firstCount + 1, fixture.Handler.RequestCount("https://test/client.jar"));
        Assert.AreEqual("client content", await File.ReadAllTextAsync(clientPath));
    }

    [TestMethod]
    [DataRow("sha1")]
    [DataRow("size")]
    [DataRow("extra-bytes")]
    public async Task InstallerRejectsBadSha1OrSizeWithoutPublishingPartialFiles(string failure)
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(
            includeAssets: false,
            badClientSha1: failure == "sha1",
            badClientSize: failure == "size",
            oversizedClient: failure == "extra-bytes");
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        var exception = await Assert.ThrowsExactlyAsync<MojangException>(() => installer.InstallAsync(fixture.Prepared, temporary.Path));

        Assert.AreEqual(MojangError.Integrity, exception.Error);
        Assert.IsFalse(File.Exists(Path.Combine(temporary.Path, "minecraft", "versions", "install-test", "install-test.jar")));
        Assert.AreEqual(0, Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task CancellationRemovesTemporaryDownloads()
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(includeAssets: false, blockDownloads: true);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);
        using var cancellation = new CancellationTokenSource();
        var install = installer.InstallAsync(fixture.Prepared, temporary.Path, cancellationToken: cancellation.Token);

        await fixture.Handler.FirstBodyRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => install);

        Assert.AreEqual(0, Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task HttpFailureCancelsBlockedDownloadAndPreservesHttpError()
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(
            includeAssets: false,
            failingUrl: "https://test/client.jar",
            blockedUrl: "https://test/javaw.exe");
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        var installation = installer.InstallAsync(fixture.Prepared, temporary.Path);
        var exception = await Assert.ThrowsExactlyAsync<MojangException>(() => installation.WaitAsync(TimeSpan.FromSeconds(5)));
        await fixture.Handler.BlockingReadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(MojangError.Http, exception.Error);
        Assert.AreEqual(500, exception.StatusCode);
        Assert.AreEqual(0, Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task UserCancellationTakesPriorityOverAFailFastDownloadError()
    {
        using var temporary = new TestDirectory();
        using var cancellation = new CancellationTokenSource();
        var fixture = CreateInstallFixture(
            includeAssets: false,
            failingUrl: "https://test/client.jar",
            blockedUrl: "https://test/javaw.exe",
            cancelOnFailFast: cancellation);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);
        var installation = installer.InstallAsync(fixture.Prepared, temporary.Path, cancellationToken: cancellation.Token);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => installation.WaitAsync(TimeSpan.FromSeconds(5)));
        await fixture.Handler.BlockingReadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
    }

    [TestMethod]
    public async Task InstallerLimitsConcurrentDownloadsToFour()
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(includeAssets: false, gateDownloads: true);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);
        var installation = installer.InstallAsync(fixture.Prepared, temporary.Path);

        await fixture.Handler.FourConcurrentResponsesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(4, fixture.Handler.MaximumConcurrentResponses);
        fixture.Handler.ReleaseGatedResponses();
        await installation;

        Assert.IsTrue(fixture.Handler.MaximumConcurrentResponses <= 4);
    }

    [TestMethod]
    public async Task InstallationProgressIsMonotonicAndReachesTheTotal()
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(includeAssets: false);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);
        var progress = new RecordingProgress();

        await installer.InstallAsync(fixture.Prepared, temporary.Path, progress);

        var reports = progress.Reports;
        Assert.IsTrue(reports.Length > 0);
        for (var index = 1; index < reports.Length; index++)
        {
            Assert.IsTrue(reports[index].CompletedBytes >= reports[index - 1].CompletedBytes);
        }

        Assert.AreEqual(InstallationProgressStage.Complete, reports[^1].Stage);
        Assert.AreEqual(reports[^1].TotalBytes, reports[^1].CompletedBytes);
    }

    [TestMethod]
    public async Task InstallPreparationRunsOffTheCallingThread()
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(includeAssets: false);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);
        var progress = new ThreadRecordingProgress();
        var callingThread = 0;
        Task<InstallationResult>? installation = null;
        var caller = new Thread(() =>
        {
            callingThread = Environment.CurrentManagedThreadId;
            installation = installer.InstallAsync(fixture.Prepared, temporary.Path, progress);
        });
        caller.Start();
        caller.Join();

        await installation!;

        Assert.AreNotEqual(callingThread, progress.FirstThreadId);
    }

    [TestMethod]
    public async Task MojangBodyReadIdleTimeoutIsAHttpFailure()
    {
        var stalledRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new StallingBodyHandler(stalledRead));
        using var client = new MojangClient(http, readIdleTimeout: TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsExactlyAsync<MojangException>(() =>
            client.GetBytesAsync("https://test/slow-body", CancellationToken.None, expectedSize: 100));
        await stalledRead.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(MojangError.Http, exception.Error);
    }

    [TestMethod]
    public async Task MojangBodyReadIOExceptionIsAHttpFailure()
    {
        using var http = new HttpClient(new StreamHandler(() => new IOExceptionAfterChunkStream(Encoding.UTF8.GetBytes("partial"))));
        using var client = new MojangClient(http);

        var exception = await Assert.ThrowsExactlyAsync<MojangException>(() =>
            client.GetBytesAsync("https://test/broken-body", CancellationToken.None, expectedSize: 100));

        Assert.AreEqual(MojangError.Http, exception.Error);
        Assert.IsInstanceOfType<IOException>(exception.InnerException);
    }

    [TestMethod]
    public async Task MojangReadAsStreamFailureIsAHttpFailure()
    {
        using var http = new HttpClient(new StreamOpenFailureHandler());
        using var client = new MojangClient(http);

        var exception = await Assert.ThrowsExactlyAsync<MojangException>(() =>
            client.GetBytesAsync("https://test/unreadable-body", CancellationToken.None));

        Assert.AreEqual(MojangError.Http, exception.Error);
        Assert.IsInstanceOfType<IOException>(exception.InnerException);
    }

    [TestMethod]
    public async Task InstallerReadAsStreamFailureIsAHttpFailure()
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(includeAssets: false, streamOpenFailureUrl: "https://test/client.jar");
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        var exception = await Assert.ThrowsExactlyAsync<MojangException>(() => installer.InstallAsync(fixture.Prepared, temporary.Path));

        Assert.AreEqual(MojangError.Http, exception.Error);
        Assert.IsInstanceOfType<IOException>(exception.InnerException);
    }

    [TestMethod]
    public void InstallerStaleTemporaryCleanupObservesCancellation()
    {
        using var temporary = new TestDirectory();
        var javaDirectory = Path.Combine(temporary.Path, "java");
        Directory.CreateDirectory(javaDirectory);
        var staleTemporaryFile = Path.Combine(javaDirectory, ".hako-stale.tmp");
        File.WriteAllText(staleTemporaryFile, "still writing");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            MinecraftInstaller.CleanStaleTemporaryFiles(temporary.Path, cancellation.Token));
        Assert.IsTrue(File.Exists(staleTemporaryFile));
    }

    [TestMethod]
    public async Task MojangIdleTimeoutAllowsSlowResponsesWithRegularChunks()
    {
        var clock = new ManualTimeProvider();
        var stream = new ControlledChunkStream(Encoding.UTF8.GetBytes("live"));
        using var http = new HttpClient(new StreamHandler(() => stream));
        using var client = new MojangClient(http, readIdleTimeout: TimeSpan.FromSeconds(10), timeProvider: clock);

        var response = client.GetBytesAsync("https://test/live-body", CancellationToken.None, expectedSize: 4);
        for (var index = 0; index < 4; index++)
        {
            await stream.ReadStarted(index).WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(9));
            stream.Release(index);
        }

        await stream.ReadStarted(4).WaitAsync(TimeSpan.FromSeconds(5));
        stream.Release(4);

        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("live"), await response.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task AssetNamesAreValidatedOnlyWhenTheyAreMapped()
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(includeAssets: true, assetNames: ["../unused.txt"], virtualAssets: false, mapToResources: false);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        await installer.InstallAsync(fixture.Prepared, temporary.Path);

        Assert.IsFalse(File.Exists(Path.Combine(temporary.Path, "unused.txt")));
    }

    [TestMethod]
    public async Task AssetCopiesRejectCaseInsensitivePathCollisions()
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(includeAssets: true, assetNames: ["Config.json", "config.json"]);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        var exception = await Assert.ThrowsExactlyAsync<MojangException>(() => installer.InstallAsync(fixture.Prepared, temporary.Path));

        Assert.AreEqual(MojangError.Invalid, exception.Error);
    }

    [TestMethod]
    [DataRow(false, "resourcepacks")]
    [DataRow(true, "texturepacks")]
    public async Task InstallerCreatesModAndVersionAppropriatePackFolders(bool legacy, string packFolder)
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(includeAssets: false);
        var manifest = fixture.Prepared.Manifest with { Assets = legacy ? "pre-1.6" : "1.20" };
        var prepared = fixture.Prepared with { Manifest = manifest };
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        await installer.InstallAsync(prepared, temporary.Path);

        Assert.IsTrue(Directory.Exists(Path.Combine(temporary.Path, "minecraft", "mods")));
        Assert.IsTrue(Directory.Exists(Path.Combine(temporary.Path, "minecraft", packFolder)));
    }

    [TestMethod]
    public async Task InstallationRemovesOnlyOrphanedHakoTemporaryFilesWithoutFollowingJunctions()
    {
        using var temporary = new TestDirectory();
        using var outside = new TestDirectory();
        var rootTemporary = Path.Combine(temporary.Path, ".hako-root.tmp");
        var unrelatedFile = Path.Combine(temporary.Path, "minecraft", "libraries", ".hako-old.tmpx");
        var javaTemporary = Path.Combine(temporary.Path, "java", ".hako-java.tmp");
        var libraryTemporary = Path.Combine(temporary.Path, "minecraft", "libraries", ".hako-library.tmp");
        var nested = Path.Combine(temporary.Path, "minecraft", "libraries", "nested");
        var minecraftTemporary = Path.Combine(temporary.Path, "minecraft", ".hako-root-file.tmp");
        var staleDirectory = Path.Combine(temporary.Path, "minecraft", ".hako-staging.tmp");
        var savesTemporary = Path.Combine(temporary.Path, "minecraft", "saves", "World", ".hako-save.tmp");
        var modsTemporary = Path.Combine(temporary.Path, "minecraft", "mods", ".hako-mod.tmp");
        var screenshotsTemporary = Path.Combine(temporary.Path, "minecraft", "screenshots", ".hako-shot.tmp");
        var resourcepacksTemporary = Path.Combine(temporary.Path, "minecraft", "resourcepacks", ".hako-pack.tmp");
        var outsideTemporary = Path.Combine(outside.Path, ".hako-outside.tmp");
        var junction = Path.Combine(temporary.Path, "minecraft", "libraries", "redirected");
        Directory.CreateDirectory(Path.GetDirectoryName(javaTemporary)!);
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.GetDirectoryName(savesTemporary)!);
        Directory.CreateDirectory(Path.GetDirectoryName(modsTemporary)!);
        Directory.CreateDirectory(Path.GetDirectoryName(screenshotsTemporary)!);
        Directory.CreateDirectory(Path.GetDirectoryName(resourcepacksTemporary)!);
        Directory.CreateDirectory(staleDirectory);
        await File.WriteAllTextAsync(rootTemporary, "keep");
        await File.WriteAllTextAsync(javaTemporary, "stale");
        await File.WriteAllTextAsync(libraryTemporary, "stale");
        await File.WriteAllTextAsync(minecraftTemporary, "stale");
        await File.WriteAllTextAsync(Path.Combine(staleDirectory, "partial"), "stale");
        await File.WriteAllTextAsync(Path.Combine(nested, ".hako-nested.tmp"), "stale");
        await File.WriteAllTextAsync(savesTemporary, "keep");
        await File.WriteAllTextAsync(modsTemporary, "keep");
        await File.WriteAllTextAsync(screenshotsTemporary, "keep");
        await File.WriteAllTextAsync(resourcepacksTemporary, "keep");
        await File.WriteAllTextAsync(unrelatedFile, "keep");
        await File.WriteAllTextAsync(outsideTemporary, "keep");
        CreateDirectorySymlink(junction, outside.Path);
        var fixture = CreateInstallFixture(includeAssets: false);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        await installer.InstallAsync(fixture.Prepared, temporary.Path);

        Assert.IsFalse(File.Exists(javaTemporary));
        Assert.IsFalse(File.Exists(libraryTemporary));
        Assert.IsFalse(File.Exists(minecraftTemporary));
        Assert.IsFalse(Directory.Exists(staleDirectory));
        Assert.IsFalse(File.Exists(Path.Combine(nested, ".hako-nested.tmp")));
        Assert.IsTrue(File.Exists(rootTemporary));
        Assert.IsTrue(File.Exists(savesTemporary));
        Assert.IsTrue(File.Exists(modsTemporary));
        Assert.IsTrue(File.Exists(screenshotsTemporary));
        Assert.IsTrue(File.Exists(resourcepacksTemporary));
        Assert.IsTrue(File.Exists(unrelatedFile));
        Assert.IsTrue(File.Exists(outsideTemporary));
        Directory.Delete(junction);
    }

    [TestMethod]
    public async Task InstallationContinuesWhenStaleTemporaryFileIsInUse()
    {
        using var temporary = new TestDirectory();
        var libraryDirectory = Path.Combine(temporary.Path, "minecraft", "libraries");
        Directory.CreateDirectory(libraryDirectory);
        var occupiedPath = Path.Combine(libraryDirectory, ".hako-occupied.tmp");
        await File.WriteAllTextAsync(occupiedPath, "keep while open");
        using var occupied = new FileStream(occupiedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var fixture = CreateInstallFixture(includeAssets: false);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        await installer.InstallAsync(fixture.Prepared, temporary.Path);

        Assert.IsTrue(File.Exists(occupiedPath));
        Assert.IsTrue(File.Exists(Path.Combine(libraryDirectory, "org", "test", "library.jar")));
    }

    [TestMethod]
    public async Task InstallationRejectsAReparsePointInAnInstallPath()
    {
        using var temporary = new TestDirectory();
        using var outside = new TestDirectory();
        var minecraftLink = Path.Combine(temporary.Path, "minecraft");
        CreateDirectorySymlink(minecraftLink, outside.Path);
        var fixture = CreateInstallFixture(includeAssets: false);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        var exception = await Assert.ThrowsExactlyAsync<InstanceStorageException>(() => installer.InstallAsync(fixture.Prepared, temporary.Path));

        Assert.AreEqual(InstanceStorageError.ReparsePointNotAllowed, exception.Error);
        Assert.AreEqual(0, Directory.EnumerateFiles(outside.Path, "*", SearchOption.AllDirectories).Count());
        Directory.Delete(minecraftLink);
    }

    [TestMethod]
    [DataRow("../escape.dll")]
    [DataRow("/absolute.dll")]
    [DataRow("C:/outside.dll")]
    [DataRow("folder/../../escape.dll")]
    [DataRow("CON.txt")]
    [DataRow("folder./native.dll")]
    [DataRow("file:stream.dll")]
    [DataRow("duplicate.dll|DUPLICATE.DLL")]
    public async Task LegacyNativeArchivesRejectUnsafePathsBeforeExtraction(string entryPath)
    {
        using var temporary = new TestDirectory();
        var zip = CreateZip(entryPath.Split('|'));
        var library = CreateNativeLibrary(zip, ["META-INF/"]);
        var fixture = CreateInstallFixture(includeAssets: false, libraries: [library], nativeArchiveBytes: zip);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        var exception = await Assert.ThrowsExactlyAsync<MojangException>(() => installer.InstallAsync(fixture.Prepared, temporary.Path));

        Assert.AreEqual(MojangError.Invalid, exception.Error);
        Assert.IsFalse(File.Exists(Path.Combine(temporary.Path, "minecraft", "natives", "escape.dll")));
        Assert.AreEqual(0, Directory.Exists(Path.Combine(temporary.Path, "minecraft", "natives"))
            ? Directory.EnumerateFiles(Path.Combine(temporary.Path, "minecraft", "natives"), "*", SearchOption.AllDirectories).Count()
            : 0);
    }

    [TestMethod]
    public async Task LegacyNativeArchiveHonorsMetaInfExclusion()
    {
        using var temporary = new TestDirectory();
        var zip = CreateZip("META-INF/MANIFEST.MF", "org/test/native.dll");
        var fixture = CreateInstallFixture(
            includeAssets: false,
            libraries: [CreateNativeLibrary(zip, ["META-INF/"])],
            nativeArchiveBytes: zip);
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        await installer.InstallAsync(fixture.Prepared, temporary.Path);

        Assert.IsFalse(File.Exists(Path.Combine(temporary.Path, "minecraft", "natives", "META-INF", "MANIFEST.MF")));
        Assert.IsTrue(File.Exists(Path.Combine(temporary.Path, "minecraft", "natives", "org", "test", "native.dll")));
    }

    [TestMethod]
    public async Task FailureInSecondNativeArchiveDoesNotPublishAnyStagedFiles()
    {
        using var temporary = new TestDirectory();
        var nativeDirectory = Path.Combine(temporary.Path, "minecraft", "natives");
        Directory.CreateDirectory(nativeDirectory);
        var original = Path.Combine(nativeDirectory, "original.dll");
        await File.WriteAllTextAsync(original, "original");
        var firstArchive = CreateZip("org/test/first.dll");
        var secondArchive = Encoding.UTF8.GetBytes("not a ZIP archive");
        const string firstUrl = "https://test/native-first.zip";
        const string secondUrl = "https://test/native-second.zip";
        var fixture = CreateInstallFixture(
            includeAssets: false,
            libraries:
            [
                CreateNativeLibrary(firstArchive, ["META-INF/"], firstUrl, "org/test/native-first.jar"),
                CreateNativeLibrary(secondArchive, ["META-INF/"], secondUrl, "org/test/native-second.jar")
            ],
            downloadPayloadOverrides: new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [firstUrl] = firstArchive,
                [secondUrl] = secondArchive
            });
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(fixture.Prepared, temporary.Path));

        Assert.AreEqual("original", await File.ReadAllTextAsync(original));
        Assert.IsFalse(File.Exists(Path.Combine(nativeDirectory, "org", "test", "first.dll")));
        Assert.AreEqual(1, Directory.EnumerateFiles(nativeDirectory, "*", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task NativePublicationFailureRestoresPreviousDirectory()
    {
        using var temporary = new TestDirectory();
        var minecraftDirectory = Path.Combine(temporary.Path, "minecraft");
        var nativeDirectory = Path.Combine(minecraftDirectory, "natives");
        var stagingDirectory = Path.Combine(minecraftDirectory, ".hako-natives-new.tmp");
        Directory.CreateDirectory(nativeDirectory);
        Directory.CreateDirectory(stagingDirectory);
        var original = Path.Combine(nativeDirectory, "original.dll");
        var staged = Path.Combine(stagingDirectory, "new.dll");
        await File.WriteAllTextAsync(original, "original");
        await File.WriteAllTextAsync(staged, "new");
        var moveCount = 0;

        void MoveDirectory(string source, string destination)
        {
            if (Interlocked.Increment(ref moveCount) == 2)
            {
                throw new IOException("Injected publication failure.");
            }

            Directory.Move(source, destination);
        }

        var exception = Assert.ThrowsExactly<IOException>(() =>
            MinecraftInstaller.PublishNativeStaging(stagingDirectory, temporary.Path, MoveDirectory));

        Assert.AreEqual("Injected publication failure.", exception.Message);
        Assert.AreEqual("original", await File.ReadAllTextAsync(original));
        Assert.IsFalse(File.Exists(Path.Combine(nativeDirectory, "new.dll")));
        Assert.IsTrue(File.Exists(staged));
    }

    [TestMethod]
    public async Task JavaRuntimeLinksMustStayInsideJavaAndAreNotCreatedAsReparsePoints()
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(includeAssets: false);
        var files = new Dictionary<string, MojangJavaRuntimeEntry>(fixture.Prepared.RuntimeManifest.Files)
        {
            ["bin/java.exe"] = new() { Type = "link", Target = "../../outside.exe" }
        };
        var prepared = fixture.Prepared with { RuntimeManifest = new MojangJavaRuntimeManifest { Files = files } };
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        var exception = await Assert.ThrowsExactlyAsync<MojangException>(() => installer.InstallAsync(prepared, temporary.Path));

        Assert.AreEqual(MojangError.Invalid, exception.Error);
        Assert.IsFalse(File.Exists(Path.Combine(temporary.Path, "outside.exe")));
        Assert.AreEqual(0, Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task CancellationDuringNativeEntryExtractionPublishesNoPartialFile()
    {
        using var temporary = new TestDirectory();
        var destinationDirectory = Path.Combine(temporary.Path, "minecraft", "natives");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, "native.dll");
        using var cancellation = new CancellationTokenSource();
        await using var source = new CancelAfterFirstChunkStream(Encoding.UTF8.GetBytes("native bytes"), cancellation);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            MinecraftInstaller.ExtractNativeEntryAsync(source, destination, source.Length, cancellation.Token));

        Assert.IsFalse(File.Exists(destination));
        Assert.AreEqual(0, Directory.EnumerateFiles(temporary.Path, ".hako-*.tmp", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task PrepareRejectsJavaRuntimeLinkEntriesWithAUsefulError()
    {
        var exception = await Assert.ThrowsExactlyAsync<MojangException>(() => PrepareSyntheticAsync(
            [("lwjgl", GameArchitecture.X64)],
            [GameArchitecture.X64],
            new HostArchitecture(HostMachineArchitecture.X64, false),
            includeRuntimeLink: true));

        Assert.AreEqual(MojangError.Invalid, exception.Error);
        StringAssert.Contains(exception.Message, "link");
    }

    [TestMethod]
    public async Task NonHttpsDownloadUrlIsRejectedBeforeARequestIsMade()
    {
        using var temporary = new TestDirectory();
        var fixture = CreateInstallFixture(includeAssets: false, clientUrl: "http://test/client.jar");
        using var client = new MojangClient(new HttpClient(fixture.Handler));
        var installer = new MinecraftInstaller(client);

        var exception = await Assert.ThrowsExactlyAsync<MojangException>(() => installer.InstallAsync(fixture.Prepared, temporary.Path));

        Assert.AreEqual(MojangError.Invalid, exception.Error);
        Assert.AreEqual(0, fixture.Handler.RequestCount("http://test/client.jar"));
    }

    private static async Task<PreparedInstallation> PrepareSyntheticAsync(
        IReadOnlyCollection<(string Group, GameArchitecture Architecture)> nativeVariants,
        IReadOnlyCollection<GameArchitecture> runtimeArchitectures,
        HostArchitecture host,
        GameArchitecture? pinned = null,
        bool includeRuntimeLink = false)
    {
        var runtimeFiles = new JsonObject
        {
            ["bin/javaw.exe"] = new JsonObject
            {
                ["type"] = "file",
                ["downloads"] = new JsonObject
                {
                    ["raw"] = new JsonObject
                    {
                        ["url"] = "https://test/javaw.exe",
                        ["sha1"] = Hash("java"),
                        ["size"] = 4
                    }
                }
            }
        };
        if (includeRuntimeLink)
        {
            runtimeFiles["bin/linked.dll"] = new JsonObject { ["type"] = "link", ["target"] = "../linked.dll" };
        }

        var runtimeBytes = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["files"] = runtimeFiles });
        var indexBytes = BuildRuntimeIndex(runtimeBytes, runtimeArchitectures);
        var versionBytes = BuildVersionManifest(nativeVariants);
        const string versionUrl = "https://test/version.json";
        var handler = new PayloadHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [versionUrl] = versionBytes,
            [MojangClient.RuntimeIndexUrl] = indexBytes,
            ["https://test/runtime-manifest.json"] = runtimeBytes
        });
        using var client = new MojangClient(new HttpClient(handler));
        var entry = new MojangVersionEntry { Id = "test", Type = "release", Url = versionUrl, Sha1 = Hash(versionBytes) };
        return pinned is { } architecture
            ? await client.PrepareAsync(entry, host, architecture)
            : await client.PrepareAsync(entry, host);
    }

    private static byte[] BuildVersionManifest(IReadOnlyCollection<(string Group, GameArchitecture Architecture)> nativeVariants)
    {
        var nativeBytes = Encoding.UTF8.GetBytes("native bytes");
        var libraries = nativeVariants.Select(variant =>
        {
            var classifier = variant.Architecture switch
            {
                GameArchitecture.Arm64 => "natives-windows-arm64",
                GameArchitecture.X64 => "natives-windows",
                GameArchitecture.X86 => "natives-windows-x86",
                _ => throw new ArgumentOutOfRangeException()
            };
            var path = $"org/test/{variant.Group}/1.0/{variant.Group}-1.0-{classifier}.jar";
            return $"{{\"name\":\"org.test:{variant.Group}:1.0:{classifier}\",\"downloads\":{{\"artifact\":{{\"path\":\"{path}\",\"url\":\"https://test/native.jar\",\"sha1\":\"{Hash(nativeBytes)}\",\"size\":{nativeBytes.Length}}}}},\"rules\":[{{\"action\":\"allow\",\"os\":{{\"name\":\"windows\"}}}}]}}";
        });
        var json = "{\"id\":\"test\",\"javaVersion\":{\"component\":\"java-runtime-gamma\",\"majorVersion\":17},"
            + "\"downloads\":{\"client\":{\"url\":\"https://test/client.jar\",\"sha1\":\"" + Hash("client") + "\",\"size\":6}},"
            + "\"libraries\":[" + string.Join(',', libraries) + "]}";
        return Encoding.UTF8.GetBytes(json);
    }

    private static byte[] BuildRuntimeIndex(byte[] runtimeManifest, IReadOnlyCollection<GameArchitecture> architectures)
    {
        var package = "[ {\"manifest\":{\"url\":\"https://test/runtime-manifest.json\",\"sha1\":\"" + Hash(runtimeManifest)
            + "\",\"size\":" + runtimeManifest.Length + "},\"version\":{\"name\":\"17.0.15\",\"released\":\"2025-01-01\"}} ]";
        var platforms = architectures.Select(architecture => "\"" + architecture.MojangRuntimeKey() + "\":{\"java-runtime-gamma\":" + package + "}");
        return Encoding.UTF8.GetBytes("{" + string.Join(',', platforms) + "}");
    }

    private static byte[] RewriteRuntimeFixture(byte[] runtimeIndexBytes, byte[] runtimeManifest)
    {
        var root = JsonNode.Parse(runtimeIndexBytes)!.AsObject();
        foreach (var platform in root.Where(property => property.Key.StartsWith("windows-", StringComparison.Ordinal)))
        {
            foreach (var component in platform.Value!.AsObject())
            {
                foreach (var package in component.Value!.AsArray())
                {
                    var manifest = package!["manifest"]!.AsObject();
                    manifest["url"] = "https://test/runtime-manifest.json";
                    manifest["sha1"] = Hash(runtimeManifest);
                    manifest["size"] = runtimeManifest.Length;
                }
            }
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static InstallFixture CreateInstallFixture(
        bool includeAssets,
        bool badClientSha1 = false,
        bool badClientSize = false,
        bool oversizedClient = false,
        string clientUrl = "https://test/client.jar",
        bool blockDownloads = false,
        string? failingUrl = null,
        string? blockedUrl = null,
        bool gateDownloads = false,
        IReadOnlyList<string>? assetNames = null,
        bool virtualAssets = true,
        bool mapToResources = true,
        IReadOnlyCollection<LibraryInstallation>? libraries = null,
        byte[]? nativeArchiveBytes = null,
        IReadOnlyDictionary<string, byte[]>? downloadPayloadOverrides = null,
        CancellationTokenSource? cancelOnFailFast = null,
        string? streamOpenFailureUrl = null)
    {
        var javaBytes = Encoding.UTF8.GetBytes("javaw content");
        var clientBytes = Encoding.UTF8.GetBytes("client content");
        var libraryBytes = Encoding.UTF8.GetBytes("library content");
        var logBytes = Encoding.UTF8.GetBytes("log config");
        var assetBytes = Encoding.UTF8.GetBytes("asset content");
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["https://test/javaw.exe"] = javaBytes,
            [clientUrl] = clientBytes,
            ["https://test/library.jar"] = libraryBytes,
            ["https://test/log-config.xml"] = logBytes
        };
        MojangAssetIndexReference? assetReference = null;
        if (includeAssets)
        {
            var assetHash = Hash(assetBytes);
            var names = assetNames ?? ["lang/en_us.json"];
            var objects = names.ToDictionary(name => name, _ => new { hash = assetHash, size = assetBytes.Length }, StringComparer.Ordinal);
            var indexBytes = JsonSerializer.SerializeToUtf8Bytes(new { objects, @virtual = virtualAssets, map_to_resources = mapToResources });
            payloads["https://test/asset-index.json"] = indexBytes;
            payloads[MojangClient.AssetBaseUrl + assetHash[..2] + "/" + assetHash] = assetBytes;
            assetReference = new MojangAssetIndexReference
            {
                Id = "test-index",
                Url = "https://test/asset-index.json",
                Sha1 = Hash(indexBytes),
                Size = indexBytes.Length
            };
        }

        if (libraries is not null)
        {
            foreach (var library in libraries)
            {
                payloads[library.Download.Url] = downloadPayloadOverrides is not null
                    && downloadPayloadOverrides.TryGetValue(library.Download.Url, out var overrideBytes)
                    ? overrideBytes
                    : nativeArchiveBytes ?? libraryBytes;
            }
        }

        var clientDownload = new MojangDownload
        {
            Url = clientUrl,
            Sha1 = badClientSha1 ? Hash("incorrect") : Hash(clientBytes),
            Size = badClientSize ? clientBytes.Length + 1 : oversizedClient ? clientBytes.Length - 1 : clientBytes.Length
        };
        var manifest = new MojangVersionMetadata
        {
            Id = "install-test",
            JavaVersion = new MojangJavaRequirement { Component = "java-runtime-gamma", MajorVersion = 17 },
            Downloads = new Dictionary<string, MojangDownload>(StringComparer.OrdinalIgnoreCase) { ["client"] = clientDownload },
            Libraries = [],
            AssetIndex = assetReference,
            Logging = new Dictionary<string, MojangLoggingConfiguration>
            {
                ["client"] = new()
                {
                    Argument = "-Dlog4j.configurationFile=${path}",
                    File = new MojangDownload
                    {
                        Id = "client.xml",
                        Url = "https://test/log-config.xml",
                        Sha1 = Hash(logBytes),
                        Size = logBytes.Length
                    }
                }
            }
        };
        var runtimeManifest = new MojangJavaRuntimeManifest
        {
            Files = new Dictionary<string, MojangJavaRuntimeEntry>(StringComparer.Ordinal)
            {
                ["bin/javaw.exe"] = new()
                {
                    Type = "file",
                    Downloads = new Dictionary<string, MojangDownload>
                    {
                        ["raw"] = new() { Url = "https://test/javaw.exe", Sha1 = Hash(javaBytes), Size = javaBytes.Length }
                    }
                }
            }
        };
        var versionData = Encoding.UTF8.GetBytes("{\"id\":\"install-test\"}");
        var runtimeData = Encoding.UTF8.GetBytes(
            "{\"files\":{\"bin/javaw.exe\":{\"type\":\"file\",\"downloads\":{\"raw\":{\"url\":\"https://test/javaw.exe\",\"sha1\":\""
            + Hash(javaBytes) + "\",\"size\":" + javaBytes.Length + "}}}}}");
        var prepared = new PreparedInstallation(
            new MojangVersionEntry { Id = manifest.Id, Url = "https://test/version.json", Sha1 = Hash(versionData) },
            GameArchitecture.X64,
            manifest,
            versionData,
            new MojangJavaRuntimePackage
            {
                Manifest = new MojangDownload { Url = "https://test/runtime-manifest.json", Sha1 = Hash(runtimeData), Size = runtimeData.Length },
                Version = new MojangRuntimeVersion { Name = "17.0.15", Released = "2025-01-01" }
            },
            runtimeManifest,
            runtimeData,
            libraries?.ToArray() ?? [new LibraryInstallation(
                new MojangDownload { Url = "https://test/library.jar", Sha1 = Hash(libraryBytes), Size = libraryBytes.Length },
                "org/test/library.jar")]);

        return new InstallFixture(
            prepared,
            new PayloadHandler(payloads, blockDownloads, failingUrl, blockedUrl, gateDownloads, cancelOnFailFast, streamOpenFailureUrl),
            clientBytes);
    }

    private static LibraryInstallation CreateNativeLibrary(
        byte[] zipBytes,
        IReadOnlyList<string> excludes,
        string url = "https://test/native.zip",
        string? path = null) =>
        new(new MojangDownload
        {
            Url = url,
            Sha1 = Hash(zipBytes),
            Size = zipBytes.Length
        }, path ?? "org/test/native.jar", IsNative: true, ExtractNatives: true, ExtractionExcludes: excludes);

    private static byte[] CreateZip(params string[] entryNames)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entryName in entryNames)
            {
                var entry = archive.CreateEntry(entryName);
                using var stream = entry.Open();
                if (!entryName.EndsWith('/'))
                {
                    stream.Write(Encoding.UTF8.GetBytes("content"));
                }
            }
        }

        return memory.ToArray();
    }

    private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
    private static MojangDownload Download(string path) => new()
    {
        Path = path,
        Url = "https://test/" + path,
        Sha1 = Hash("content"),
        Size = 7
    };

    private sealed record InstallFixture(PreparedInstallation Prepared, PayloadHandler Handler, byte[] ClientBytes);

    private sealed class PayloadHandler(
        Dictionary<string, byte[]> payloads,
        bool blockDownloads = false,
        string? failingUrl = null,
        string? blockedUrl = null,
        bool gateDownloads = false,
        CancellationTokenSource? cancelOnFailFast = null,
        string? streamOpenFailureUrl = null) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _requests = new(StringComparer.Ordinal);
        private readonly ConcurrentBag<string> _userAgents = [];
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeResponses;
        private int _maximumConcurrentResponses;
        public TaskCompletionSource FirstBodyRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockingReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FourConcurrentResponsesStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyCollection<string> UserAgents => _userAgents.ToArray();
        public int RequestCount(string url) => _requests.GetValueOrDefault(url);
        public int MaximumConcurrentResponses => Volatile.Read(ref _maximumConcurrentResponses);
        public void ReleaseGatedResponses() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            _requests.AddOrUpdate(url, 1, static (_, count) => count + 1);
            if (request.Headers.UserAgent.FirstOrDefault() is { } userAgent)
            {
                _userAgents.Add(userAgent.ToString());
            }

            if (string.Equals(url, failingUrl, StringComparison.Ordinal))
            {
                await FirstBodyRead.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = cancelOnFailFast is null ? null : new CancelOnDisposeContent(cancelOnFailFast)
                };
            }

            if (string.Equals(url, streamOpenFailureUrl, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamOpenFailureContent() };
            }

            if (!payloads.TryGetValue(url, out var bytes))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (gateDownloads)
            {
                var active = Interlocked.Increment(ref _activeResponses);
                UpdateMaximum(active);
                if (active == 4)
                {
                    FourConcurrentResponsesStarted.TrySetResult();
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new GatedContent(bytes, _gate.Task, () => Interlocked.Decrement(ref _activeResponses))
                };
            }

            var shouldBlock = string.Equals(url, blockedUrl, StringComparison.Ordinal)
                || blockDownloads && (url is "https://test/javaw.exe" or "https://test/client.jar");
            HttpContent content = shouldBlock
                ? new StreamContent(new DelayedReadStream(bytes, FirstBodyRead, BlockingReadCancelled))
                : new ByteArrayContent(bytes);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private void UpdateMaximum(int active)
        {
            var current = Volatile.Read(ref _maximumConcurrentResponses);
            while (active > current)
            {
                var previous = Interlocked.CompareExchange(ref _maximumConcurrentResponses, active, current);
                if (previous == current)
                {
                    break;
                }

                current = previous;
            }
        }
    }

    private sealed class DelayedReadStream(byte[] bytes, TaskCompletionSource firstRead, TaskCompletionSource cancelled) : Stream
    {
        private bool _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read)
            {
                return WaitForCancellationAsync(cancellationToken, cancelled);
            }

            _read = true;
            var count = Math.Min(buffer.Length, bytes.Length);
            bytes.AsMemory(0, count).CopyTo(buffer);
            firstRead.TrySetResult();
            return ValueTask.FromResult(count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private static async ValueTask<int> WaitForCancellationAsync(CancellationToken cancellationToken, TaskCompletionSource cancelled)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class GatedContent(byte[] bytes, Task gate, Action disposed) : HttpContent
    {
        private int _isDisposed;

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(bytes.AsMemory()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new GatedReadStream(bytes, gate));

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                disposed();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class GatedReadStream(byte[] bytes, Task gate) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class StallingBodyHandler(TaskCompletionSource stalledRead) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingBodyStream(Encoding.UTF8.GetBytes("body"), stalledRead))
            });
    }

    private sealed class StallingBodyStream(byte[] bytes, TaskCompletionSource stalledRead) : MemoryStream(bytes, writable: false)
    {
        private bool _firstRead = true;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_firstRead)
            {
                _firstRead = false;
                return await base.ReadAsync(buffer, cancellationToken);
            }

            stalledRead.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class StreamHandler(Func<Stream> createStream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(createStream())
            });
    }

    private sealed class StreamOpenFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamOpenFailureContent() });
    }

    private sealed class StreamOpenFailureContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromException<Stream>(new IOException("The response stream could not be opened."));
    }

    private sealed class IOExceptionAfterChunkStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        private bool _firstRead = true;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_firstRead)
            {
                _firstRead = false;
                return base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
            }

            return ValueTask.FromException<int>(new IOException("The response body ended unexpectedly."));
        }
    }

    private sealed class ControlledChunkStream(byte[] bytes) : Stream
    {
        private readonly TaskCompletionSource[] _readStarted =
            Enumerable.Range(0, bytes.Length + 1).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private readonly TaskCompletionSource[] _released =
            Enumerable.Range(0, bytes.Length + 1).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private int _nextRead;

        public Task ReadStarted(int index) => _readStarted[index].Task;
        public void Release(int index) => _released[index].TrySetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _nextRead) - 1;
            _readStarted[index].TrySetResult();
            await _released[index].Task.WaitAsync(cancellationToken);
            if (index == bytes.Length)
            {
                return 0;
            }

            bytes.AsMemory(index, 1).CopyTo(buffer);
            return 1;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];

        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] timers;
            lock (_gate)
            {
                timers = _timers.ToArray();
            }

            foreach (var timer in timers)
            {
                timer.Advance(elapsed);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            timer.Change(dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private readonly object _gate = new();
            private TimeSpan? _remaining;
            private TimeSpan _period;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    _remaining = dueTime == Timeout.InfiniteTimeSpan ? null : dueTime;
                    _period = period;
                    return true;
                }
            }

            public void Advance(TimeSpan elapsed)
            {
                TimerCallback? callbackToRun = null;
                lock (_gate)
                {
                    if (_disposed || _remaining is not { } remaining)
                    {
                        return;
                    }

                    if (remaining > elapsed)
                    {
                        _remaining = remaining - elapsed;
                        return;
                    }

                    callbackToRun = callback;
                    _remaining = _period > TimeSpan.Zero ? _period : null;
                }

                callbackToRun?.Invoke(state);
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    _disposed = true;
                    _remaining = null;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class CancelOnDisposeContent(CancellationTokenSource cancellation) : HttpContent
    {
        private int _disposed;

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                cancellation.Cancel();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CancelAfterFirstChunkStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes, writable: false)
    {
        private bool _firstRead = true;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_firstRead)
            {
                _firstRead = false;
                return base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
            }

            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
    }

    private sealed class RecordingProgress : IProgress<InstallationProgress>
    {
        private readonly ConcurrentQueue<InstallationProgress> _reports = new();
        public InstallationProgress[] Reports => _reports.ToArray();
        public void Report(InstallationProgress value) => _reports.Enqueue(value);
    }

    private sealed class ThreadRecordingProgress : IProgress<InstallationProgress>
    {
        private int _firstThreadId;
        public int FirstThreadId => Volatile.Read(ref _firstThreadId);

        public void Report(InstallationProgress value) =>
            Interlocked.CompareExchange(ref _firstThreadId, Environment.CurrentManagedThreadId, 0);
    }

    private static void CreateDirectorySymlink(string path, string target)
    {
        if (!CreateSymbolicLinkW(path, target, SymbolicLinkFlagDirectory | SymbolicLinkFlagAllowUnprivilegedCreate))
        {
            throw new IOException($"Could not create a test directory symlink (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    private const uint SymbolicLinkFlagDirectory = 0x1;
    private const uint SymbolicLinkFlagAllowUnprivilegedCreate = 0x2;

    [DllImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateSymbolicLinkW(string linkPath, string targetPath, uint flags);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Hako.MinecraftTests." + Guid.NewGuid().ToString("N"));
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
