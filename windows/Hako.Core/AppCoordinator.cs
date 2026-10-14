using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Hako.Core;

public enum LauncherScreen
{
    Loading,
    Welcome,
    Login,
    Builds,
    Settings
}

public enum SettingsSection
{
    General,
    Profile
}

public sealed class AppCoordinator(IUserSettingsStore settingsStore) : INotifyPropertyChanged
{
    private LauncherScreen _screen = LauncherScreen.Loading;
    private AppLanguage _language = AppLanguages.Default;
    private bool _isGuest;
    private SettingsSection _settingsSection = SettingsSection.General;
    private LauncherScreen _loginReturnScreen = LauncherScreen.Welcome;
    private SettingsSection _loginReturnSection = SettingsSection.General;

    public event PropertyChangedEventHandler? PropertyChanged;

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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        Language = AppLanguages.Parse(settings?.Language);
        Screen = LauncherScreen.Welcome;
    }

    public void ShowWelcomeAfterStartupFailure()
    {
        Language = AppLanguages.Default;
        Screen = LauncherScreen.Welcome;
    }

    public async Task SetLanguageAsync(AppLanguage language, CancellationToken cancellationToken = default)
    {
        var previousLanguage = Language;
        Language = language;
        try
        {
            await settingsStore.SaveAsync(new UserSettings(language.ToTag()), cancellationToken);
        }
        catch
        {
            Language = previousLanguage;
            throw;
        }
    }

    public void BeginSignIn()
    {
        _loginReturnScreen = Screen;
        _loginReturnSection = SelectedSettingsSection;
        Screen = LauncherScreen.Login;
    }

    public void BackFromSignIn()
    {
        Screen = _loginReturnScreen;
        SelectedSettingsSection = _loginReturnSection;
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

    public void OpenSettings(SettingsSection section = SettingsSection.General)
    {
        SelectedSettingsSection = section;
        Screen = LauncherScreen.Settings;
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
