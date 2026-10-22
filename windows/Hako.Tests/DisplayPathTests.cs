namespace Hako.Tests;

[TestClass]
public sealed class DisplayPathTests
{
    [TestMethod]
    public void ShortensOnlyPathsInsideUserProfile()
    {
        Assert.AreEqual(@"%USERPROFILE%\.hako\Build", DisplayPath.Format(@"C:\Users\Alex\.hako\Build", @"C:\Users\Alex"));
        Assert.AreEqual(@"%USERPROFILE%\.hako\Build", DisplayPath.Format(@"c:\users\ALEX\.hako\Build", @"C:\Users\Alex\"));
        Assert.AreEqual(@"C:\Users\Alexander\.hako\Build", DisplayPath.Format(@"C:\Users\Alexander\.hako\Build", @"C:\Users\Alex"));
        Assert.AreEqual(@"D:\Temp\hako-preview\Build", DisplayPath.Format(@"D:\Temp\hako-preview\Build", @"C:\Users\Alex"));
        Assert.AreEqual(@"C:\Users\Alex\.hako\Build", DisplayPath.Format(@"C:\Users\Alex\.hako\Build", ""));
    }
}
