using Hako.Core;
using System.Runtime.Versioning;

namespace Hako.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class UserSettingsServiceTests
{
    [TestMethod]
    public async Task GameParametersAndLanguagePersistTogetherInEitherUpdateOrder()
    {
        using var temporary = new TemporaryDirectory();
        var store = new JsonUserSettingsStore(temporary.Path);
        var service = new UserSettingsService(store);
        await service.LoadAsync();
        var defaults = new GameLaunchDefaults("C:\\Java\\bin\\java.exe", "-Djava.test=1", "--demo", true, 1920, 1080, 2048);

        await service.SetGameLaunchDefaultsAsync(defaults);
        await service.SetLanguageAsync(AppLanguage.English);
        AssertSettings(await store.LoadAsync(), AppLanguage.English, defaults);

        await service.SetLanguageAsync(AppLanguage.Russian);
        var laterDefaults = defaults with { Fullscreen = false, WindowWidth = 1600, MaximumMemoryMiB = 1024 };
        await service.SetGameLaunchDefaultsAsync(laterDefaults);
        AssertSettings(await store.LoadAsync(), AppLanguage.Russian, laterDefaults);
    }

    [TestMethod]
    public async Task ConcurrentLanguageAndGameParameterSavesKeepTheCompleteLatestSnapshot()
    {
        var store = new PausingSettingsStore(new UserSettings("ru"));
        var service = new UserSettingsService(store);
        await service.LoadAsync();
        var defaults = new GameLaunchDefaults("java.exe", "-Xms512M", "--quickPlay", false, 1440, 900, 1024);

        var gameSave = service.SetGameLaunchDefaultsAsync(defaults);
        await store.FirstSaveReached.Task;
        var languageSave = service.SetLanguageAsync(AppLanguage.English);
        store.ReleaseFirstSave.TrySetResult();
        await Task.WhenAll(gameSave, languageSave);

        AssertSettings(store.Settings, AppLanguage.English, defaults);
        AssertSettings(service.Current, AppLanguage.English, defaults);
    }

    [TestMethod]
    public async Task FailedSaveRollsBackBeforeAQueuedSaveCapturesItsSnapshot()
    {
        var store = new FailingFirstThenPausingSecondSettingsStore(new UserSettings("ru"));
        var service = new UserSettingsService(store);
        await service.LoadAsync();
        var defaults = new GameLaunchDefaults("java.exe", "-Xms512M", "--demo", false, 1440, 900, 1024);

        var firstSave = service.SetLanguageAsync(AppLanguage.English);
        await store.FirstSaveReached.Task;
        var secondSave = service.SetGameLaunchDefaultsAsync(defaults);
        Assert.AreEqual(defaults.JavaPath, service.Current.JavaPath);
        store.ReleaseFirstSave.TrySetResult();

        await store.SecondSaveReached.Task;
        await Assert.ThrowsExactlyAsync<IOException>(() => firstSave);
        store.ReleaseSecondSave.TrySetResult();
        await secondSave;

        Assert.AreEqual("ru", store.Settings?.Language);
        Assert.AreEqual("ru", service.Current.Language);
        Assert.AreEqual(defaults.JavaPath, store.Settings?.JavaPath);
    }

    [TestMethod]
    public async Task SettingsFileContainingOnlyLanguageRemainsReadable()
    {
        using var temporary = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temporary.Path, "settings.json"), "{\"language\":\"en\"}");
        var service = new UserSettingsService(new JsonUserSettingsStore(temporary.Path));

        var loaded = await service.LoadAsync();
        var defaults = service.GetGameLaunchDefaults();

        Assert.AreEqual(new UserSettings("en"), loaded);
        Assert.AreEqual(AppLanguage.English, AppLanguages.Parse(service.Current.Language));
        Assert.AreEqual("", defaults.JavaPath);
        Assert.AreEqual("", defaults.JavaArguments);
        Assert.AreEqual("", defaults.MinecraftArguments);
        Assert.IsFalse(defaults.Fullscreen);
        Assert.AreEqual(1280, defaults.WindowWidth);
        Assert.AreEqual(720, defaults.WindowHeight);
        Assert.IsTrue(defaults.MaximumMemoryMiB is >= 512 and <= 4096);
    }

    [TestMethod]
    public async Task FailedLoadIsRetriedBeforeLanguageSaveAndNeverOverwritesUnreadableSettings()
    {
        var store = new FailingLoadSettingsStore(new UserSettings(
            "ru", "C:\\Java\\bin\\java.exe", "-Xms2G", "--demo", true, 1920, 1080, 4096));
        var service = new UserSettingsService(store);

        await Assert.ThrowsExactlyAsync<IOException>(() => service.LoadAsync());
        await Assert.ThrowsExactlyAsync<IOException>(() => service.SetLanguageAsync(AppLanguage.English));

        Assert.AreEqual(2, store.LoadCount);
        Assert.AreEqual(0, store.SaveCount);
        Assert.AreEqual("ru", store.PersistedSettings?.Language);
        Assert.AreEqual("C:\\Java\\bin\\java.exe", store.PersistedSettings?.JavaPath);
        Assert.AreEqual(4096, store.PersistedSettings?.MaximumMemoryMiB);
    }

    [TestMethod]
    public async Task SuccessfulRetryMergesLanguageWithSettingsReadFromDisk()
    {
        var store = new FailingLoadSettingsStore(new UserSettings(
            "ru", "C:\\Java\\bin\\java.exe", "-Xms2G", "--demo", true, 1920, 1080, 4096))
        {
            FailLoadCount = 1
        };
        var service = new UserSettingsService(store);

        await Assert.ThrowsExactlyAsync<IOException>(() => service.LoadAsync());
        await service.SetLanguageAsync(AppLanguage.English);

        Assert.AreEqual(2, store.LoadCount);
        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual("en", store.PersistedSettings?.Language);
        Assert.AreEqual("C:\\Java\\bin\\java.exe", store.PersistedSettings?.JavaPath);
        Assert.AreEqual("-Xms2G", store.PersistedSettings?.JavaArguments);
        Assert.AreEqual("--demo", store.PersistedSettings?.MinecraftArguments);
        Assert.AreEqual(true, store.PersistedSettings?.Fullscreen);
        Assert.AreEqual(4096, store.PersistedSettings?.MaximumMemoryMiB);
    }

    [TestMethod]
    public async Task CorruptSettingsCanBeReplacedByTheFullCurrentSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var settingsPath = Path.Combine(temporary.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{ broken");
        var store = new JsonUserSettingsStore(temporary.Path);
        var service = new UserSettingsService(store);

        await Assert.ThrowsExactlyAsync<System.Text.Json.JsonException>(() => service.LoadAsync());
        await service.SetLanguageAsync(AppLanguage.English);

        var saved = await store.LoadAsync();
        Assert.IsNotNull(saved);
        Assert.AreEqual("en", saved.Language);
        Assert.AreEqual(service.Current, saved);
    }

    [TestMethod]
    public void NonpositiveMaximumMemoryUsesTheStandardInitialValue()
    {
        Assert.AreEqual(GameLaunchDefaults.Standard.MaximumMemoryMiB, GameLaunchDefaults.NormalizeMemory(0));
        Assert.AreEqual(GameLaunchDefaults.Standard.MaximumMemoryMiB, GameLaunchDefaults.NormalizeMemory(-1));
        Assert.AreEqual(GameLaunchDefaults.Standard.MaximumMemoryMiB, GameLaunchDefaults.From(new UserSettings("en", MaximumMemoryMiB: 0)).MaximumMemoryMiB);
    }

    private static void AssertSettings(UserSettings? settings, AppLanguage language, GameLaunchDefaults expected)
    {
        Assert.IsNotNull(settings);
        Assert.AreEqual(language.ToTag(), settings.Language);
        Assert.AreEqual(expected.JavaPath, settings.JavaPath);
        Assert.AreEqual(expected.JavaArguments, settings.JavaArguments);
        Assert.AreEqual(expected.MinecraftArguments, settings.MinecraftArguments);
        Assert.AreEqual(expected.Fullscreen, settings.Fullscreen);
        Assert.AreEqual(expected.WindowWidth, settings.WindowWidth);
        Assert.AreEqual(expected.WindowHeight, settings.WindowHeight);
        Assert.AreEqual(GameLaunchDefaults.NormalizeMemory(expected.MaximumMemoryMiB), settings.MaximumMemoryMiB);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Hako.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FailingLoadSettingsStore(UserSettings persistedSettings) : IUserSettingsStore
    {
        public UserSettings? PersistedSettings { get; private set; } = persistedSettings;
        public int FailLoadCount { get; set; } = int.MaxValue;
        public int LoadCount { get; private set; }
        public int SaveCount { get; private set; }

        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            if (LoadCount <= FailLoadCount)
            {
                throw new IOException("Injected settings read failure.");
            }

            return Task.FromResult(PersistedSettings);
        }

        public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            PersistedSettings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingFirstThenPausingSecondSettingsStore(UserSettings? settings) : IUserSettingsStore
    {
        private int _saveCount;

        public UserSettings? Settings { get; private set; } = settings;
        public TaskCompletionSource FirstSaveReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSaveReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecondSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);

        public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
        {
            var save = Interlocked.Increment(ref _saveCount);
            if (save == 1)
            {
                FirstSaveReached.TrySetResult();
                await ReleaseFirstSave.Task.WaitAsync(cancellationToken);
                throw new IOException("Injected first settings write failure.");
            }

            if (save == 2)
            {
                SecondSaveReached.TrySetResult();
                await ReleaseSecondSave.Task.WaitAsync(cancellationToken);
            }

            Settings = settings;
        }
    }
}
