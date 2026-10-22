using Hako.Core;

namespace Hako.Tests;

[TestClass]
public sealed class GameLaunchDefaultsTests
{
    [TestMethod]
    [DataRow("1280", 1280)]
    [DataRow("1", 1)]
    [DataRow("0", null)]
    [DataRow("-720", null)]
    [DataRow("+720", null)]
    [DataRow(" 720", null)]
    [DataRow("720.5", null)]
    [DataRow("1 280", null)]
    [DataRow("", null)]
    [DataRow("99999999999", null)]
    public void WindowDimensionAcceptsOnlyPositiveIntegers(string text, int? expected)
    {
        Assert.AreEqual(expected, GameLaunchDefaults.WindowDimension(text));
    }
}
