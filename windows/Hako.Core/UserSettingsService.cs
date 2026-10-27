using System.Text.Json;

namespace Hako.Core;

public sealed class UserSettingsService(IUserSettingsStore store)
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _sync = new();
    private UserSettings _snapshot = new(null);
    private UserSettings _savedSnapshot = new(null);
    private bool _loadFailed;
    private long _languageSelection;
    private long _gameSettingsSelection;

    public UserSettings Current
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    public string? SavedLanguage
    {
        get
        {
            lock (_sync)
            {
                return _savedSnapshot.Language;
            }
        }
    }

    public async Task<UserSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                var loaded = await store.LoadAsync(cancellationToken);
                lock (_sync)
                {
                    _snapshot = loaded ?? new UserSettings(null);
                    _savedSnapshot = _snapshot;
                    _loadFailed = false;
                }

                return loaded;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                lock (_sync)
                {
                    _loadFailed = true;
                }

                throw;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task SetLanguageAsync(AppLanguage language, CancellationToken cancellationToken = default)
    {
        long selection;
        lock (_sync)
        {
            selection = ++_languageSelection;
            _snapshot = _snapshot with { Language = language.ToTag() };
        }

        await SaveSnapshotAsync(cancellationToken, () =>
        {
            lock (_sync)
            {
                if (selection == _languageSelection)
                {
                    _snapshot = _snapshot with { Language = _savedSnapshot.Language };
                }
            }
        });
    }

    public async Task SetGameLaunchDefaultsAsync(GameLaunchDefaults defaults, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        long selection;
        lock (_sync)
        {
            selection = ++_gameSettingsSelection;
            _snapshot = WithGameDefaults(_snapshot, defaults);
        }

        await SaveSnapshotAsync(cancellationToken, () =>
        {
            lock (_sync)
            {
                if (selection == _gameSettingsSelection)
                {
                    _snapshot = WithGameFields(_snapshot, _savedSnapshot);
                }
            }
        });
    }

    public GameLaunchDefaults GetGameLaunchDefaults() => GameLaunchDefaults.From(Current);

    /// <summary>Waits for already submitted language and game-settings writes before application replacement.</summary>
    public async Task WaitForPendingSavesAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _saveGate.Release();
    }

    private async Task SaveSnapshotAsync(CancellationToken cancellationToken, Action rollback)
    {
        var acquired = false;
        try
        {
            await _saveGate.WaitAsync(cancellationToken);
            acquired = true;
            UserSettings snapshot;
            bool loadFailed;
            lock (_sync)
            {
                snapshot = _snapshot;
                loadFailed = _loadFailed;
            }

            if (loadFailed)
            {
                UserSettings loaded;
                try
                {
                    loaded = await store.LoadAsync(cancellationToken) ?? new UserSettings(null);
                }
                catch (JsonException)
                {
                    loaded = new UserSettings(null);
                }

                lock (_sync)
                {
                    _savedSnapshot = loaded;
                    _snapshot = MergeNonNullSettings(loaded, _snapshot);
                    _loadFailed = false;
                    snapshot = _snapshot;
                }
            }

            await store.SaveAsync(snapshot, cancellationToken);
            lock (_sync)
            {
                _savedSnapshot = snapshot;
            }
        }
        catch
        {
            if (!acquired)
            {
                await _saveGate.WaitAsync(CancellationToken.None);
                acquired = true;
            }

            rollback();
            throw;
        }
        finally
        {
            if (acquired)
            {
                _saveGate.Release();
            }
        }
    }

    private static UserSettings WithGameDefaults(UserSettings settings, GameLaunchDefaults defaults) => settings with
    {
        JavaPath = defaults.JavaPath,
        JavaArguments = defaults.JavaArguments,
        MinecraftArguments = defaults.MinecraftArguments,
        Fullscreen = defaults.Fullscreen,
        WindowWidth = defaults.WindowWidth,
        WindowHeight = defaults.WindowHeight,
        MaximumMemoryMiB = defaults.MaximumMemoryMiB
    };

    private static UserSettings WithGameFields(UserSettings target, UserSettings source) => target with
    {
        JavaPath = source.JavaPath,
        JavaArguments = source.JavaArguments,
        MinecraftArguments = source.MinecraftArguments,
        Fullscreen = source.Fullscreen,
        WindowWidth = source.WindowWidth,
        WindowHeight = source.WindowHeight,
        MaximumMemoryMiB = source.MaximumMemoryMiB
    };

    private static UserSettings MergeNonNullSettings(UserSettings source, UserSettings changes) => new(
        changes.Language ?? source.Language,
        changes.JavaPath ?? source.JavaPath,
        changes.JavaArguments ?? source.JavaArguments,
        changes.MinecraftArguments ?? source.MinecraftArguments,
        changes.Fullscreen ?? source.Fullscreen,
        changes.WindowWidth ?? source.WindowWidth,
        changes.WindowHeight ?? source.WindowHeight,
        changes.MaximumMemoryMiB ?? source.MaximumMemoryMiB);
}
