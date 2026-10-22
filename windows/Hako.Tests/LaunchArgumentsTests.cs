using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
public sealed class LaunchArgumentsTests
{
    [TestMethod]
    public void ParseSupportsQuotedAndEscapedArgumentsWithoutShellExpansion()
    {
        var parsed = LaunchArguments.Parse("\"path with spaces\" 'single quoted' escaped\\ value $HOME");

        CollectionAssert.AreEqual(new[] { "path with spaces", "single quoted", "escaped value", "$HOME" }, parsed.ToArray());
    }

    [TestMethod]
    public void ParseAllowsEmptyQuotedArgumentAndRejectsUnclosedEscapeOrQuote()
    {
        CollectionAssert.AreEqual(new[] { "", "next" }, LaunchArguments.Parse("\"\" next").ToArray());
        Assert.AreEqual("LaunchArgumentsInvalid", Assert.ThrowsExactly<LaunchArgumentException>(() => LaunchArguments.Parse("\"open")).ResourceKey);
        Assert.ThrowsExactly<LaunchArgumentException>(() => LaunchArguments.Parse("tail\\"));
    }

    [TestMethod]
    public void ApplyingMemoryReplacesAllMaximumsAndClampsInitialHeap()
    {
        var policy = new JavaMemoryPolicy(16UL * 1024 * 1024 * 1024);
        var result = LaunchArguments.ApplyMemory(
            ["-Xmx1G", "-Xmx", "2048M", "-XX:MaxHeapSize=2G", "-Xms4G", "-XX:InitialHeapSize=8G", "-Dtest=yes"],
            1024,
            GameArchitecture.X64,
            policy);

        Assert.AreEqual(1, result.Count(argument => argument.StartsWith("-Xmx", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { "-Xms1024M", "-XX:InitialHeapSize=1024M", "-Dtest=yes", "-Xmx1024M" },
            result.Where(argument => argument is "-Xms1024M" or "-XX:InitialHeapSize=1024M" or "-Dtest=yes" or "-Xmx1024M").ToArray());
        Assert.IsFalse(result.Any(argument => argument.StartsWith("-XX:MaxHeapSize=", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void MemoryPolicyUsesPhysicalMemoryMinimumStepWarningAndX86Limit()
    {
        const ulong mib = 1024 * 1024;
        var policy = new JavaMemoryPolicy(1024 * mib);
        Assert.AreEqual(512, policy.Normalize(0, GameArchitecture.X64));
        Assert.AreEqual(768, policy.Normalize(900, GameArchitecture.X64));
        Assert.IsTrue(policy.Warns(768));
        Assert.IsFalse(policy.Warns(512));

        var largeMemory = new JavaMemoryPolicy(64UL * 1024 * 1024 * 1024);
        Assert.AreEqual(1536, largeMemory.MaximumMiB(GameArchitecture.X86));
        Assert.AreEqual(8192, largeMemory.Normalize(8192, GameArchitecture.X64));
    }
}
