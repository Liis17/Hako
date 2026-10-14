using Hako.Core;
using System.Runtime.Versioning;
using System.Text;

namespace Hako.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class StorageTests
{
    [TestMethod]
    public async Task AccountSessionRoundTripsThroughCurrentUserDpapiWithoutPlaintextTokens()
    {
        var directory = NewDirectory();
        try
        {
            var store = new AccountSessionStore(directory);
            var account = TestData.Account();
            Assert.IsTrue(await store.SaveAsync(account, Replace));

            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "session.dat"));
            var storedText = Encoding.UTF8.GetString(bytes);
            Assert.IsFalse(storedText.Contains(account.MicrosoftRefreshToken, StringComparison.Ordinal));
            Assert.IsFalse(storedText.Contains(account.MinecraftAccessToken!, StringComparison.Ordinal));
            Assert.AreEqual(account, await store.LoadAsync());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task AccountSessionClearDeletesProtectedSession()
    {
        var directory = NewDirectory();
        try
        {
            var store = new AccountSessionStore(directory);
            Assert.IsTrue(await store.SaveAsync(TestData.Account(), Replace));
            Assert.IsTrue(await store.ClearAsync(Replace));
            Assert.IsFalse(File.Exists(Path.Combine(directory, "session.dat")));
            Assert.IsNull(await store.LoadAsync());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task CorruptProtectedSessionReturnsRecoverableStoreError()
    {
        var directory = NewDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "session.dat"), "not protected account data");
            var store = new AccountSessionStore(directory);

            await Assert.ThrowsExactlyAsync<AccountStoreException>(() => store.LoadAsync());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task FailedFinalFileReplaceDoesNotReportSuccessfulSave()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "session.dat"));
            var store = new AccountSessionStore(directory);

            await Assert.ThrowsExactlyAsync<AccountStoreException>(() => store.SaveAsync(TestData.Account(), Replace));

            Assert.IsTrue(Directory.Exists(Path.Combine(directory, "session.dat")));
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task SettingsStoreSerializesOnlyChosenLanguageAndRoundTrips()
    {
        var directory = NewDirectory();
        try
        {
            var store = new JsonUserSettingsStore(directory);
            await store.SaveAsync(new UserSettings("en"));

            var json = await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"));
            Assert.IsTrue(json.Contains("\"language\": \"en\"", StringComparison.Ordinal));
            Assert.AreEqual(new UserSettings("en"), await store.LoadAsync());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task CorruptSettingsReportPreferencesUnavailableAndUseDefaultLanguage()
    {
        var directory = NewDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{ not json");
            var coordinator = new AppCoordinator(new JsonUserSettingsStore(directory), new MemoryAccountStore(), new SequenceAuthService());

            await coordinator.InitializeAsync();

            Assert.AreEqual(StartupNotice.PreferencesUnavailable, coordinator.CurrentStartupNotice);
            Assert.AreEqual(AppLanguages.Default, coordinator.Language);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static bool Replace(Action replace)
    {
        replace();
        return true;
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Hako.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
