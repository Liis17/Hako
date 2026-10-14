using Hako.Core;
using System.Xml.Linq;

namespace Hako.Tests;

[TestClass]
public sealed class LocalizationResourcesTests
{
    [TestMethod]
    public void RussianAndEnglishResourcesHaveTheSameNonemptyKeys()
    {
        var russian = ReadResourceNames(Path.Combine(AppContext.BaseDirectory, "Resources", "ru-RU", "Resources.resw"));
        var english = ReadResourceNames(Path.Combine(AppContext.BaseDirectory, "Resources", "en-US", "Resources.resw"));

        CollectionAssert.AreEquivalent(russian, english);
        Assert.IsTrue(russian.Count > 0);
        Assert.IsFalse(russian.Any(string.IsNullOrWhiteSpace));
    }

    [TestMethod]
    public void EveryMappedErrorAndStartupNoticeHasRussianAndEnglishText()
    {
        var russian = ReadResourceNames(Path.Combine(AppContext.BaseDirectory, "Resources", "ru-RU", "Resources.resw"));
        var english = ReadResourceNames(Path.Combine(AppContext.BaseDirectory, "Resources", "en-US", "Resources.resw"));
        var keys = Enum.GetValues<AuthErrorKind>().Select(error => error.ResourceKey())
            .Concat(Enum.GetValues<StartupNotice>().Select(notice => notice.ResourceKey()));

        foreach (var key in keys)
        {
            CollectionAssert.Contains(russian, key);
            CollectionAssert.Contains(english, key);
        }
    }

    private static List<string> ReadResourceNames(string path) => XDocument.Load(path)
        .Root!
        .Elements("data")
        .Select(element => (string?)element.Attribute("name"))
        .Where(name => name is not null)
        .Select(name => name!)
        .ToList();
}
