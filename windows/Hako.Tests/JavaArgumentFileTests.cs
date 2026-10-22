using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
public sealed class JavaArgumentFileTests
{
    [TestMethod]
    public void JavaArgumentFileEscapesQuotesAndWindowsBackslashes()
    {
        Assert.AreEqual("\"C:\\\\folder with space\\\\file\\\"name\"",
            GameProcessRunner.FormatArgumentFileValue("C:\\folder with space\\file\"name"));
        Assert.AreEqual("\"\"", GameProcessRunner.FormatArgumentFileValue(""));
        Assert.AreEqual("Кириллица", GameProcessRunner.FormatArgumentFileValue("Кириллица"));
    }

    [TestMethod]
    public async Task RealJavaReadsLongArgumentFileWithPathsAndEscapingWhenAvailable()
    {
        var java = FindJava("java.exe");
        var javac = FindJava("javac.exe");
        var jarTool = FindJava("jar.exe");
        if (java is null || javac is null || jarTool is null)
        {
            Assert.Inconclusive("A local JDK with java.exe, javac.exe, and jar.exe is not installed; the test does not download Java.");
        }

        using var temporary = new TemporaryDirectory();
        var major = await new JavaLaunchValidation().ValidateAsync(java!, 8, GameArchitecture.X64, TimeSpan.FromSeconds(10));
        // Builds launch bin/javaw.exe: its properties must also reach the redirected probe output.
        var javaw = Path.Combine(Path.GetDirectoryName(java!)!, "javaw.exe");
        if (File.Exists(javaw))
        {
            Assert.AreEqual(major, await new JavaLaunchValidation().ValidateAsync(javaw, 8, GameArchitecture.X64, TimeSpan.FromSeconds(10)));
        }

        var codePage = JavaAnsiEncoding.CurrentCodePage;
        var supportsCyrillic = JavaAnsiEncoding.CanRepresent("Кириллица", codePage);
        var instanceRoot = Path.Combine(temporary.Root, supportsCyrillic ? "Кириллица space" : "Argument File Space", "backslash folder", "instance");
        var libraryRoot = Path.Combine(instanceRoot, "minecraft", "libraries");
        Directory.CreateDirectory(libraryRoot);
        var jarPath = CompileProbe(javac!, jarTool!, libraryRoot);
        var relativeJarPath = Path.GetRelativePath(libraryRoot, jarPath).Replace('\\', '/');
        var libraries = Enumerable.Range(0, 650)
            .Select(_ => new LibraryInstallation(new MojangDownload(), relativeJarPath))
            .ToArray();

        var expectedArguments = new List<string> { "argument with spaces", @"C:\back\slash\file.txt", new string('x', 1200) };
        if (supportsCyrillic)
        {
            expectedArguments.Add("Привет Minecraft");
        }

        var manifest = JsonSerializer.Deserialize<MojangVersionMetadata>("""
            {"id":"argfile-probe","mainClass":"HakoArgumentEcho",
             "arguments":{"jvm":["-cp","${classpath}"],"game":[]}}
            """, MojangJsonContext.Default.MojangVersionMetadata)!;
        var parameters = new InstanceParameters
        {
            MaximumMemoryMiB = 512,
            MinecraftArguments = LaunchArguments.Format(expectedArguments)
        };
        var launchPlan = MinecraftLaunchPlan.Build(manifest, instanceRoot, java!, MinecraftLaunchIdentity.Offline("Player"),
            LaunchArgumentSource.Custom, parameters, GameArchitecture.X64, libraries: libraries, javaMajorVersion: major);
        var classpath = launchPlan.Arguments[Array.IndexOf(launchPlan.Arguments.ToArray(), "-cp") + 1];
        Assert.IsTrue(classpath.Length > Win32CommandLineLimit, "The fixture must exceed the Windows command-line limit.");

        if (!supportsCyrillic)
        {
            var unsupported = MinecraftLaunchPlan.Build(manifest, instanceRoot, java!, MinecraftLaunchIdentity.Offline("Player"),
                LaunchArgumentSource.Custom,
                parameters with { MinecraftArguments = LaunchArguments.Format(expectedArguments.Append("Кириллица")) },
                GameArchitecture.X64, libraries: libraries, javaMajorVersion: major);
            var encodingError = await Assert.ThrowsExactlyAsync<MinecraftLaunchException>(() =>
                new GameProcessRunner().StartAsync(unsupported, Guid.NewGuid(), instanceRoot));
            Assert.AreEqual(MinecraftLaunchError.ArgumentFileEncoding, encodingError.Error);
        }

        var process = await new GameProcessRunner().StartAsync(launchPlan, Guid.NewGuid(), instanceRoot);
        Assert.AreEqual(0, await process.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        var actual = File.ReadAllLines(process.LogPath).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line))).ToArray();

        CollectionAssert.AreEqual(expectedArguments, actual[^expectedArguments.Count..]);
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(instanceRoot, "minecraft", ".hako-launch"), "*.args").Length);
        Assert.IsFalse(File.Exists(Path.Combine(instanceRoot, "minecraft", ".hako-running.json")));
        if (!supportsCyrillic)
        {
            Assert.Inconclusive($"The ANSI code page {codePage} can't represent Cyrillic; the Cyrillic instance path was not checked with real Java.");
        }
    }

    [TestMethod]
    public void JavaArgumentFileQuotesApostrophesAndCommentMarkers()
    {
        Assert.AreEqual("\"O'Neil\"", GameProcessRunner.FormatArgumentFileValue("O'Neil"));
        Assert.AreEqual("\"#foo\"", GameProcessRunner.FormatArgumentFileValue("#foo"));
        Assert.AreEqual("\"a#b\"", GameProcessRunner.FormatArgumentFileValue("a#b"));
    }

    [TestMethod]
    public void ArgumentFileUsesStrictAnsiCodePage()
    {
        string[] arguments = ["-Dname=Привет", "C:\\Игры\\Hako"];

        var cyrillic = GameProcessRunner.EncodeArgumentFile(arguments, 1251);
        CollectionAssert.AreEqual(new byte[] { 0xcf, 0xf0, 0xe8 }, cyrillic[7..10]);

        var western = Assert.ThrowsExactly<MinecraftLaunchException>(() => GameProcessRunner.EncodeArgumentFile(arguments, 1252));
        Assert.AreEqual(MinecraftLaunchError.ArgumentFileEncoding, western.Error);

        var utf8 = GameProcessRunner.EncodeArgumentFile(arguments, JavaAnsiEncoding.Utf8CodePage);
        CollectionAssert.AreEqual(new UTF8Encoding(false).GetBytes("-Dname=Привет\r\n\"C:\\\\Игры\\\\Hako\"\r\n"), utf8);
    }

    [TestMethod]
    public void AnsiEncodingRejectsBestFitSubstitutions()
    {
        // Windows-1252 best-fit maps U+0100 (Ā) to 'A' and U+221E (∞) to '8'.
        Assert.IsFalse(JavaAnsiEncoding.CanRepresent("\u0100", 1252));
        Assert.IsFalse(JavaAnsiEncoding.CanRepresent("\u221e", 1252));
        Assert.IsFalse(JavaAnsiEncoding.CanRepresent("\ud800", JavaAnsiEncoding.Utf8CodePage));
        Assert.IsTrue(JavaAnsiEncoding.CanRepresent("é", 1252));
    }

    [TestMethod]
    public void Java8CommandLineMustBeRepresentableInAnsiCodePage()
    {
        var plan = new MinecraftLaunchPlan("C:\\Java\\bin\\javaw.exe", ["-Dname=Привет", "Main"], "C:\\", 8);

        Assert.AreEqual("-Dname=Привет", GameProcessRunner.PrepareArguments(plan, 1251).Arguments[0]);
        var error = Assert.ThrowsExactly<MinecraftLaunchException>(() => GameProcessRunner.PrepareArguments(plan, 1252));
        Assert.AreEqual(MinecraftLaunchError.ArgumentFileEncoding, error.Error);
    }

    [TestMethod]
    public void UnrepresentablePathsUseShortNamesOrFail()
    {
        static string? ShortPath(string path) => path switch
        {
            "C:\\Игры\\lib.jar" => "C:\\B1B2~1\\lib.jar",
            "C:\\Игры" => "C:\\B1B2~1",
            _ => null
        };
        var plan = new MinecraftLaunchPlan("C:\\Java\\bin\\javaw.exe",
            ["-cp", "C:\\Игры\\lib.jar;C:\\ascii.jar", "-Djava.library.path=C:\\Игры", "--gameDir", "C:\\Игры", "Main"], "C:\\", 17);

        var arguments = GameProcessRunner.PrepareArguments(plan, 1252, ShortPath).Arguments;

        CollectionAssert.AreEqual(new[]
        {
            "-cp", "C:\\B1B2~1\\lib.jar;C:\\ascii.jar", "-Djava.library.path=C:\\B1B2~1", "--gameDir", "C:\\B1B2~1", "Main"
        }, arguments.ToArray());
        Assert.AreEqual("@C:\\B1B2~1", JavaAnsiEncoding.AdaptPaths("@C:\\Игры", 1252, ShortPath));
        var error = Assert.ThrowsExactly<MinecraftLaunchException>(() =>
            JavaAnsiEncoding.AdaptPaths("C:\\Нет 8.3", 1252, ShortPath));
        Assert.AreEqual(MinecraftLaunchError.ArgumentFileEncoding, error.Error);
    }

    private const int Win32CommandLineLimit = 32767;

    private static string? FindJava(string executableName)
    {
        var candidates = new List<string>();
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            candidates.Add(Path.Combine(javaHome, "bin", executableName));
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                candidates.Add(Path.Combine(directory.Trim('"'), executableName));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string CompileProbe(string javac, string jarTool, string root)
    {
        var sourceDirectory = Path.Combine(root, "source with spaces");
        var classesDirectory = Path.Combine(root, "class output");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(classesDirectory);
        var source = Path.Combine(sourceDirectory, "HakoArgumentEcho.java");
        File.WriteAllText(source, """
            import java.nio.charset.StandardCharsets;
            import java.util.Base64;
            public class HakoArgumentEcho {
              public static void main(String[] args) {
                for (String arg : args) System.out.println(Base64.getEncoder().encodeToString(arg.getBytes(StandardCharsets.UTF_8)));
              }
            }
            """, new UTF8Encoding(false));
        RunTool(javac, ["-d", classesDirectory, source]);
        var jarPath = Path.Combine(root, "probe with spaces.jar");
        RunTool(jarTool, ["cf", jarPath, "-C", classesDirectory, "."]);
        return jarPath;
    }

    private static void RunTool(string executable, IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
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

        Assert.IsTrue(process.Start());
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, stdout + stderr);
    }
}
