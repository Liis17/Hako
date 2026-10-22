using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
using Hako;
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

    [TestMethod]
    public void UiExceptionsMapToSpecificLocalizedErrors()
    {
        var russian = ReadResourceNames(Path.Combine(AppContext.BaseDirectory, "Resources", "ru-RU", "Resources.resw"));
        var english = ReadResourceNames(Path.Combine(AppContext.BaseDirectory, "Resources", "en-US", "Resources.resw"));
        var keys = Enum.GetValues<InstanceStorageError>().Select(error => error.ResourceKey())
            .Concat(Enum.GetValues<MojangError>().Select(error => error.ResourceKey()))
            .ToList();

        foreach (var key in keys)
        {
            Assert.AreNotEqual("InstallationErrorUnexpected", key);
            CollectionAssert.Contains(russian, key);
            CollectionAssert.Contains(english, key);
        }

        Assert.AreEqual("InstallationErrorStorageDuplicateBuildName", InstallationResourceKeys.ErrorResourceKey(
            new InstanceStorageException(InstanceStorageError.DuplicateBuildName, "duplicate")));
        Assert.AreEqual("InstanceRegistryDamaged", InstallationResourceKeys.ErrorResourceKey(
            new InstanceRegistryException("damaged")));
        Assert.AreEqual("InstallationErrorMojangUnsupported", InstallationResourceKeys.ErrorResourceKey(
            new MojangException(MojangError.Unsupported, "unsupported")));
        Assert.AreEqual("InstallationErrorMojangHttp", InstallationResourceKeys.ErrorResourceKey(new HttpRequestException()));
        Assert.AreEqual("InstallationErrorStorageOperationFailed", InstallationResourceKeys.ErrorResourceKey(new IOException("in use")));
        Assert.AreEqual("InstallationErrorUnexpected", InstallationResourceKeys.ErrorResourceKey(new InvalidOperationException()));
    }

    [TestMethod]
    public void EveryLaunchErrorShownByTheProfileHasRussianAndEnglishText()
    {
        var russian = ReadResourceNames(Path.Combine(AppContext.BaseDirectory, "Resources", "ru-RU", "Resources.resw"));
        var english = ReadResourceNames(Path.Combine(AppContext.BaseDirectory, "Resources", "en-US", "Resources.resw"));
        var keys = Enum.GetValues<MinecraftLaunchError>().Select(error => new MinecraftLaunchException(error).ResourceKey)
            .Concat(Enum.GetValues<JavaValidationError>().Select(error => new JavaLaunchValidationException(error).ResourceKey))
            .Concat(Enum.GetValues<InstanceRegistryError>().Select(error => new InstanceRegistryException(error, "registry").ResourceKey))
            // Keys GameLaunchCoordinator assigns directly to GameRunState.
            .Concat(["LaunchProcessRecordInvalid", "LaunchProcessStartFailed", "LaunchGameExitCode"]);

        foreach (var key in keys)
        {
            CollectionAssert.Contains(russian, key);
            CollectionAssert.Contains(english, key);
        }
    }

    [TestMethod]
    public void EveryContentConfirmationNoteAndFailureCodeMapsToRussianAndEnglishResources()
    {
        var russian = ReadResourceNames(Path.Combine(AppContext.BaseDirectory, "Resources", "ru-RU", "Resources.resw"));
        var english = ReadResourceNames(Path.Combine(AppContext.BaseDirectory, "Resources", "en-US", "Resources.resw"));
        var keys = Enum.GetValues<ContentConfirmationCode>()
            .SelectMany(code => new[]
            {
                ContentResourceKeys.ConfirmationTitle(code),
                ContentResourceKeys.ConfirmationMessage(code),
                ContentResourceKeys.ConfirmationPrimary(code),
                ContentResourceKeys.ConfirmationAlternative(code)
            })
            .Concat(Enum.GetValues<ContentConfirmationNoteCode>().Select(ContentResourceKeys.ConfirmationNote))
            .Concat(Enum.GetValues<ContentFailureCode>().Select(ContentResourceKeys.Failure));

        foreach (var key in keys)
        {
            CollectionAssert.Contains(russian, key);
            CollectionAssert.Contains(english, key);
        }
    }

    [TestMethod]
    [DataRow("ru-RU")]
    [DataRow("en-US")]
    public void ContentFailuresFormatArgumentsAndPreserveRecoveryDiagnostic(string language)
    {
        var strings = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources", language, "Resources.resw"))
            .Root!.Elements("data").ToDictionary(element => (string)element.Attribute("name")!, element => element.Element("value")!.Value);
        var failure = new ContentFailure(ContentFailureCode.FabricApiIncompatible,
            new Dictionary<string, string> { ["version"] = "0.100.8", ["loader"] = ">=0.16.3", ["java"] = ">=21" });

        var message = ContentResourceKeys.FormatFailureDetails(failure, key => strings[key]);
        StringAssert.Contains(message, "0.100.8");
        StringAssert.Contains(message, ">=0.16.3");
        StringAssert.Contains(message, ">=21");
        Assert.IsFalse(message.Contains('{'));
        Assert.AreEqual(ContentResourceKeys.FormatFailure(failure, key => strings[key]), message);

        var recovery = new ContentFailure(ContentFailureCode.ContentOperationFailed,
            new Dictionary<string, string> { ["diagnostic"] = @"Recovery backup: C:\isolated\.hako-backup-123" });
        Assert.AreEqual(strings["ContentFailureOperationFailed"] + "\n" + strings["ContentDiagnosticLabel"] + " " + recovery.Arguments["diagnostic"],
            ContentResourceKeys.FormatFailureDetails(recovery, key => strings[key]));
        var bulk = new ContentFailure(ContentFailureCode.BulkUpdateFailed,
            new Dictionary<string, string> { ["failureCount"] = "7" });
        StringAssert.Contains(ContentResourceKeys.FormatFailure(bulk, key => strings[key]), "7");
    }

    private static List<string> ReadResourceNames(string path) => XDocument.Load(path)
        .Root!
        .Elements("data")
        .Select(element => (string?)element.Attribute("name"))
        .Where(name => name is not null)
        .Select(name => name!)
        .ToList();
}
