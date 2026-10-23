using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Hako.Core;

public enum LauncherScreen
{
    Loading,
    Welcome,
    Login,
    Builds,
    Instance,
    InstanceCreation,
    ModpackCatalog,
    ModpackCreation,
    Settings
}

public enum SettingsSection
{
    General,
    Profile,
    Game
}

public enum StartupNotice
{
    PreferencesUnavailable,
    AccountUnavailable
}

public static class StartupNoticeExtensions
{
    public static string ResourceKey(this StartupNotice notice) => notice switch
    {
        StartupNotice.PreferencesUnavailable => "StartupPreferencesWarning",
        StartupNotice.AccountUnavailable => "StartupAccountWarning",
        _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, null)
    };
}

public sealed class AppCoordinator : INotifyPropertyChanged
{
    private readonly UserSettingsService _settingsService;
    private readonly IAccountStore _accountStore;
    private readonly IMicrosoftAuthService _authService;
    private readonly object _commitLock = new();
    private LauncherScreen _screen = LauncherScreen.Loading;
    private AppLanguage _language = AppLanguages.Default;
    private AppLanguage _savedLanguage = AppLanguages.Default;
    private bool _isGuest;
    private SettingsSection _settingsSection = SettingsSection.General;
    private Guid? _selectedInstanceId;
    private LauncherScreen _loginReturnScreen = LauncherScreen.Welcome;
    private SettingsSection _loginReturnSection = SettingsSection.General;
    private DeviceCode? _currentDeviceCode;
    private SignInPhase _signInPhase = SignInPhase.RequestingCode;
    private AuthErrorKind? _loginError;
    private StartupNotice? _startupNotice;
    private AccountSession? _account;
    private CancellationTokenSource? _activeAttempt;
    private long _attemptGeneration;
    private long _accountGeneration;
    private long _languageSelection;

    public AppCoordinator(
        IUserSettingsStore settingsStore,
        IAccountStore accountStore,
        IMicrosoftAuthService authService,
        TimeProvider? timeProvider = null)
        : this(new UserSettingsService(settingsStore), accountStore, authService, timeProvider)
    {
    }

    public AppCoordinator(
        UserSettingsService settingsService,
        IAccountStore accountStore,
        IMicrosoftAuthService authService,
        TimeProvider? timeProvider = null)
    {
        _settingsService = settingsService;
        _accountStore = accountStore;
        _authService = authService;
        MinecraftSessions = new MinecraftSessionCoordinator(this, _accountStore, _authService, timeProvider);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the calling thread when the signed-in account is replaced or invalidated.</summary>
    public event EventHandler? AccountGenerationChanged;

    public MinecraftSessionCoordinator MinecraftSessions { get; }

    public LauncherScreen Screen
    {
        get => _screen;
        private set => SetProperty(ref _screen, value);
    }

    public AppLanguage Language
    {
        get => _language;
        private set => SetProperty(ref _language, value);
    }

    public bool IsGuest
    {
        get => _isGuest;
        private set => SetProperty(ref _isGuest, value);
    }

    public SettingsSection SelectedSettingsSection
    {
        get => _settingsSection;
        private set => SetProperty(ref _settingsSection, value);
    }

    public DeviceCode? CurrentDeviceCode
    {
        get => _currentDeviceCode;
        private set => SetProperty(ref _currentDeviceCode, value);
    }

    public SignInPhase SignInPhase
    {
        get => _signInPhase;
        private set => SetProperty(ref _signInPhase, value);
    }

    public AuthErrorKind? LoginError
    {
        get => _loginError;
        private set => SetProperty(ref _loginError, value);
    }

    public StartupNotice? CurrentStartupNotice
    {
        get => _startupNotice;
        private set => SetProperty(ref _startupNotice, value);
    }

    public AccountSession? Account
    {
        get => _account;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        UserSettings? settings = null;
        try
        {
            settings = await _settingsService.LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            CurrentStartupNotice = StartupNotice.PreferencesUnavailable;
        }

        Language = AppLanguages.Parse(settings?.Language);
        _savedLanguage = Language;

        try
        {
            SetAccount(await _accountStore.LoadAsync(cancellationToken), invalidateSessions: true);
            IsGuest = false;
            Screen = Account is null ? LauncherScreen.Welcome : LauncherScreen.Builds;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            SetAccount(null, invalidateSessions: true);
            IsGuest = false;
            CurrentStartupNotice = StartupNotice.AccountUnavailable;
            Screen = LauncherScreen.Welcome;
        }
    }

    public async Task SetLanguageAsync(AppLanguage language, CancellationToken cancellationToken = default)
    {
        var selection = ++_languageSelection;
        Language = language;
        try
        {
            await _settingsService.SetLanguageAsync(language, cancellationToken);
            _savedLanguage = AppLanguages.Parse(_settingsService.SavedLanguage);
        }
        catch
        {
            _savedLanguage = AppLanguages.Parse(_settingsService.SavedLanguage);
            if (selection == _languageSelection)
            {
                Language = _savedLanguage;
            }

            throw;
        }
    }

    public Task BeginSignIn()
    {
        lock (_commitLock)
        {
            if (Screen != LauncherScreen.Login)
            {
                _loginReturnScreen = Screen;
                _loginReturnSection = SelectedSettingsSection;
            }

            Screen = LauncherScreen.Login;
        }

        return StartAttempt();
    }

    public Task RetrySignIn() => StartAttempt();

    public void BackFromSignIn()
    {
        lock (_commitLock)
        {
            InvalidateAttemptLocked();
            CurrentDeviceCode = null;
            Screen = _loginReturnScreen;
            SelectedSettingsSection = _loginReturnSection;
        }
    }

    public void CancelSignIn()
    {
        lock (_commitLock)
        {
            InvalidateAttemptLocked();
            CurrentDeviceCode = null;
        }
    }

    public void ContinueWithoutAccount()
    {
        IsGuest = true;
        Screen = LauncherScreen.Builds;
    }

    public void OpenBuilds()
    {
        Screen = LauncherScreen.Builds;
    }

    /// <summary>The build shown by <see cref="LauncherScreen.Instance"/>.</summary>
    public Guid? SelectedInstanceId
    {
        get => _selectedInstanceId;
        private set => SetProperty(ref _selectedInstanceId, value);
    }

    public void OpenInstance(Guid id)
    {
        SelectedInstanceId = id;
        Screen = LauncherScreen.Instance;
    }

    public void OpenInstanceCreation()
    {
        Screen = LauncherScreen.InstanceCreation;
    }

    public void OpenModpackCatalog()
    {
        Screen = LauncherScreen.ModpackCatalog;
    }

    public void OpenModpackCreation()
    {
        Screen = LauncherScreen.ModpackCreation;
    }

    /// <summary>Leaves the profile of a build that no longer exists.</summary>
    public void CloseInstance(Guid id)
    {
        if (SelectedInstanceId != id)
        {
            return;
        }

        SelectedInstanceId = null;
        if (Screen == LauncherScreen.Instance)
        {
            Screen = LauncherScreen.Builds;
        }
    }

    public void OpenSettings(SettingsSection section = SettingsSection.General)
    {
        SelectedSettingsSection = section;
        Screen = LauncherScreen.Settings;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (Account is null)
        {
            return;
        }

        long generation;
        lock (_commitLock)
        {
            InvalidateAttemptLocked();
            InvalidateAccountOperationsLocked();
            generation = _attemptGeneration;
        }

        var cleared = await _accountStore.ClearAsync(action =>
        {
            lock (_commitLock)
            {
                if (generation != _attemptGeneration)
                {
                    return false;
                }

                action();
                SetAccount(null, invalidateSessions: false);
                IsGuest = false;
                CurrentDeviceCode = null;
                LoginError = null;
                CurrentStartupNotice = null;
                Screen = LauncherScreen.Welcome;
                return true;
            }
        }, cancellationToken);

        if (!cleared)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private Task StartAttempt()
    {
        CancellationTokenSource attempt;
        long generation;
        lock (_commitLock)
        {
            InvalidateAccountOperationsLocked();
            InvalidateAttemptLocked();
            generation = _attemptGeneration;
            attempt = new CancellationTokenSource();
            _activeAttempt = attempt;
            CurrentDeviceCode = null;
            LoginError = null;
            SignInPhase = SignInPhase.RequestingCode;
        }

        return RunAttemptAsync(generation, attempt);
    }

    private async Task RunAttemptAsync(long generation, CancellationTokenSource attempt)
    {
        var cancellationToken = attempt.Token;
        try
        {
            var deviceCode = await _authService.RequestDeviceCodeAsync(cancellationToken);
            if (!TryUpdateAttempt(generation, attempt, () =>
                {
                    CurrentDeviceCode = deviceCode;
                    SignInPhase = SignInPhase.WaitingForCode;
                }))
            {
                return;
            }

            var token = await _authService.WaitForTokenAsync(deviceCode, cancellationToken);
            if (!TryUpdateAttempt(generation, attempt, () => SignInPhase = SignInPhase.SigningIn))
            {
                return;
            }

            var account = await _authService.SignInAsync(token, cancellationToken);
            await _accountStore.SaveAsync(account, replaceFile =>
            {
                lock (_commitLock)
                {
                    if (!IsCurrentAttempt(generation, attempt))
                    {
                        return false;
                    }

                    replaceFile();
                    SetAccount(account, invalidateSessions: true);
                    IsGuest = false;
                    CurrentDeviceCode = null;
                    LoginError = null;
                    Screen = LauncherScreen.Builds;
                    return true;
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            SetAttemptFailure(generation, attempt, AuthErrorKind.Network);
        }
        catch (AuthServiceException exception)
        {
            SetAttemptFailure(generation, attempt, exception.Kind);
        }
        catch (AccountStoreException)
        {
            SetAttemptFailure(generation, attempt, AuthErrorKind.Persistence);
        }
        catch (HttpRequestException)
        {
            SetAttemptFailure(generation, attempt, AuthErrorKind.Network);
        }
        catch (JsonException)
        {
            SetAttemptFailure(generation, attempt, AuthErrorKind.InvalidResponse);
        }
        catch
        {
            SetAttemptFailure(generation, attempt, AuthErrorKind.InvalidResponse);
        }
        finally
        {
            lock (_commitLock)
            {
                if (ReferenceEquals(_activeAttempt, attempt))
                {
                    _activeAttempt = null;
                }
            }

            attempt.Dispose();
        }
    }

    private bool TryUpdateAttempt(long generation, CancellationTokenSource attempt, Action update)
    {
        lock (_commitLock)
        {
            if (!IsCurrentAttempt(generation, attempt))
            {
                return false;
            }

            update();
            return true;
        }
    }

    private void SetAttemptFailure(long generation, CancellationTokenSource attempt, AuthErrorKind error)
    {
        TryUpdateAttempt(generation, attempt, () =>
        {
            CurrentDeviceCode = null;
            LoginError = error;
            SignInPhase = SignInPhase.Failed;
        });
    }

    private bool IsCurrentAttempt(long generation, CancellationTokenSource attempt) =>
        generation == _attemptGeneration
        && ReferenceEquals(_activeAttempt, attempt)
        && !attempt.IsCancellationRequested;

    internal (AccountSession? Account, long Generation) CaptureAccountState()
    {
        lock (_commitLock)
        {
            return (_account, _accountGeneration);
        }
    }

    internal bool TryCommitAccountUpdate(
        AccountSession expectedAccount,
        long generation,
        AccountSession updatedAccount,
        Action persist)
    {
        lock (_commitLock)
        {
            if (generation != _accountGeneration || !ReferenceEquals(_account, expectedAccount))
            {
                return false;
            }

            persist();
            SetAccount(updatedAccount, invalidateSessions: false);
            return true;
        }
    }

    private void SetAccount(AccountSession? account, bool invalidateSessions)
    {
        if (ReferenceEquals(_account, account))
        {
            return;
        }

        _account = account;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Account)));
        if (invalidateSessions)
        {
            InvalidateAccountOperationsLocked();
        }
    }

    private void InvalidateAccountOperationsLocked()
    {
        _accountGeneration++;
        var handlers = AccountGenerationChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
            }
        }
    }

    private void InvalidateAttemptLocked()
    {
        _attemptGeneration++;
        _activeAttempt?.Cancel();
        _activeAttempt = null;
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
