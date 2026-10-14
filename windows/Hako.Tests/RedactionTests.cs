using Hako.Core;

namespace Hako.Tests;

[TestClass]
public sealed class RedactionTests
{
    [TestMethod]
    public void MicrosoftTokenToStringOmitsTokens()
    {
        var text = new MicrosoftToken("ms-access-secret", "ms-refresh-secret", "user@example.com").ToString();

        Assert.IsFalse(text.Contains("ms-access-secret", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("ms-refresh-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AccountSessionToStringOmitsTokens()
    {
        var text = TestData.Account().ToString();

        Assert.IsFalse(text.Contains("refresh-secret", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("minecraft-secret", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("HakoPlayer", StringComparison.Ordinal));
    }
}
