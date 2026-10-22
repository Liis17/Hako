using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
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
            .Concat(Enum.GetValues<StartupNotice>().Select(notice => notice.ResourceKey()))
            .Concat(Enum.GetValues<InstanceInstallationState>().Select(state => state.ResourceKey()))
            .Concat(Enum.GetValues<InstallationProgressStage>().Select(stage => stage.ResourceKey()));

        foreach (var key in keys)
        {
            CollectionAssert.Contains(russian, key);
            CollectionAssert.Contains(english, key);
        }
    }

    [TestMethod]
    public void EveryInstallationErrorCodeHasRussianAndEnglishResource()
    {
        var russian = ReadResourceNames(Path.Combine(AppContext.BaseDirectory, "Resources", "ru-RU", "Resources.resw"));
        var english = ReadResourceNames(Path.Combine(AppContext.BaseDirectory, "Resources", "en-US", "Resources.resw"));
        var codes = Enum.GetValues<MojangError>().Select(error => $"mojang.{error.ToString().ToLowerInvariant()}")
            .Concat(Enum.GetValues<InstanceStorageError>().Select(error => $"storage.{error.ToString().ToLowerInvariant()}"))
            .Concat(["installation.cancelled", "installation.unexpected"]);

        foreach (var code in codes)
        {
            var key = InstallationResourceKeys.ErrorResourceKey(code);
            if (code != "installation.unexpected")
            {
                Assert.AreNotEqual("InstallationErrorUnexpected", key, code);
            }
            CollectionAssert.Contains(russian, key, code);
            CollectionAssert.Contains(english, key, code);
        }

        Assert.AreEqual("InstallationErrorMojangIntegrity", InstallationResourceKeys.ErrorResourceKey("mojang.integrity"));
        Assert.AreEqual("InstallationErrorUnexpected", InstallationResourceKeys.ErrorResourceKey("unrecognized.error"));
    }

    private static List<string> ReadResourceNames(string path) => XDocument.Load(path)
        .Root!
        .Elements("data")
        .Select(element => (string?)element.Attribute("name"))
        .Where(name => name is not null)
        .Select(name => name!)
        .ToList();
}
