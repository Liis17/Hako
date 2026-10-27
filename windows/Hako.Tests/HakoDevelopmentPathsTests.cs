using Hako.Core;

namespace Hako.Tests;

[TestClass]
public sealed class HakoDevelopmentPathsTests
{
    [TestMethod]
    public void DevelopmentPathsUseSeparateRootsAndInstanceKey()
    {
        using var temporary = new TemporaryDirectory();

        var paths = HakoPaths.CreateDevelopment(temporary.Root);

        Assert.AreEqual(Path.Combine(temporary.Root, "Hako-dev", "Game"), paths.GameRoot);
        Assert.AreEqual(Path.Combine(temporary.Root, "Hako-dev", "Data"), paths.DataRoot);
        Assert.AreEqual(Path.Combine(temporary.Root, "Hako-dev", "Cache"), paths.CacheRoot);
        Assert.IsTrue(paths.Isolated);
        Assert.AreNotEqual("Hako.Main", paths.GetInstanceKey());
        Assert.IsTrue(paths.GetInstanceKey().StartsWith("Hako.Isolated.", StringComparison.Ordinal));
    }
}
