using Hako.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Hako;

public sealed partial class HakoLoginPage : Page
{
    private AppCoordinator? _coordinator;
    private DispatcherQueueTimer? _countdownTimer;
    private HakoAuthErrorKind? _localError;
    private bool _copied;

    public HakoLoginPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _coordinator = (AppCoordinator)e.Parameter;
        _coordinator.PropertyChanged += Coordinator_PropertyChanged;
        HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
        _countdownTimer = DispatcherQueue.CreateTimer();
        _countdownTimer.Interval = TimeSpan.FromSeconds(1);
        _countdownTimer.Tick += (_, _) => RefreshCountdown();
        _countdownTimer.Start();
        RefreshStrings();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;
        if (_coordinator is not null)
        {
            _coordinator.PropertyChanged -= Coordinator_PropertyChanged;
        }

        HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
        base.OnNavigatedFrom(e);
    }

    private void Coordinator_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AppCoordinator.HakoAttemptPhase)
            or nameof(AppCoordinator.HakoAttemptError)
            or nameof(AppCoordinator.HakoBotLink)
            or nameof(AppCoordinator.HakoPendingExpiresAt)
            or nameof(AppCoordinator.HakoRequiresFreshLogin)))
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshState();
        }
        else
        {
            DispatcherQueue.TryEnqueue(RefreshState);
        }
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) RefreshStrings();
        else DispatcherQueue.TryEnqueue(RefreshStrings);
    }

    private void RefreshStrings()
    {
        BackButton.Content = HakoStrings.Get("BackButton");
        LoginTitle.Text = HakoStrings.Get("HakoAuthTitle");
        LoginInstructions.Text = HakoStrings.Get("HakoAuthInstructions");
        UsernameBox.PlaceholderText = HakoStrings.Get("HakoAuthUsernamePlaceholder");
        FreshUsernameBox.PlaceholderText = HakoStrings.Get("HakoAuthUsernamePlaceholder");
        LoginButton.Content = HakoStrings.Get("HakoAuthLoginButton");
        RegisterButton.Content = HakoStrings.Get("HakoAuthRegisterButton");
        RequestingText.Text = HakoStrings.Get("HakoAuthRequesting");
        ExchangingText.Text = HakoStrings.Get("HakoAuthExchanging");
        OpenBotButton.Content = HakoStrings.Get("HakoAuthOpenTelegram");
        CopyBotButton.Content = HakoStrings.Get(_copied ? "HakoAuthCopiedLink" : "HakoAuthCopyLink");
        RetryButton.Content = HakoStrings.Get("RetryButton");
        FreshLoginButton.Content = HakoStrings.Get("HakoAuthFreshLoginButton");
        FreshLoginNotice.Title = HakoStrings.Get("HakoAuthFreshLoginTitle");
        FreshLoginNotice.Message = HakoStrings.Get("HakoAuthFreshLoginDetails");
        PendingNotice.Message = HakoStrings.Get("HakoAuthRegistrationTelegramDetails");
        RefreshState();
    }

    private void RefreshState()
    {
        if (_coordinator is null)
        {
            return;
        }

        var hako = _coordinator.Hako;
        FormPanel.Visibility = hako.AttemptPhase == HakoAttemptPhase.Idle ? Visibility.Visible : Visibility.Collapsed;
        RequestingPanel.Visibility = hako.AttemptPhase == HakoAttemptPhase.Requesting ? Visibility.Visible : Visibility.Collapsed;
        PendingPanel.Visibility = hako.AttemptPhase == HakoAttemptPhase.Pending ? Visibility.Visible : Visibility.Collapsed;
        ExchangingPanel.Visibility = hako.AttemptPhase == HakoAttemptPhase.Exchanging ? Visibility.Visible : Visibility.Collapsed;
        FailedPanel.Visibility = hako.AttemptPhase == HakoAttemptPhase.Failed ? Visibility.Visible : Visibility.Collapsed;

        FormErrorText.Text = _localError is { } localError ? HakoStrings.Get(localError.ResourceKey()) : string.Empty;
        FormErrorText.Visibility = _localError is null ? Visibility.Collapsed : Visibility.Visible;
        FreshLoginErrorText.Text = hako.RequiresFreshLogin && _localError is { } freshLoginError
            ? HakoStrings.Get(freshLoginError.ResourceKey())
            : string.Empty;
        FreshLoginErrorText.Visibility = hako.RequiresFreshLogin && _localError is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        FailureText.Text = hako.AttemptError is { } error ? HakoStrings.Get(error.ResourceKey()) : string.Empty;
        FreshLoginNotice.IsOpen = hako.RequiresFreshLogin;
        FreshLoginPanel.Visibility = hako.RequiresFreshLogin ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = hako.RequiresFreshLogin ? Visibility.Collapsed : Visibility.Visible;
        FreshLoginButton.Visibility = hako.RequiresFreshLogin ? Visibility.Visible : Visibility.Collapsed;

        var hasBotLink = hako.BotLink is not null;
        BotLinkButtons.Visibility = hasBotLink ? Visibility.Visible : Visibility.Collapsed;
        BotLinkText.Visibility = hasBotLink ? Visibility.Visible : Visibility.Collapsed;
        BotLinkText.Text = hako.BotLink?.AbsoluteUri ?? string.Empty;
        PendingTitle.Text = hako.AuthorizationMode == HakoAuthorizationMode.Register
            ? HakoStrings.Get("HakoAuthRegistrationPending")
            : HakoStrings.Get("HakoAuthLoginPending");
        PendingNotice.IsOpen = hako.AuthorizationMode == HakoAuthorizationMode.Register;
        RefreshCountdown();
    }

    private void RefreshCountdown()
    {
        if (_coordinator is null || _coordinator.Hako.PendingExpiresAt is null)
        {
            PendingCountdown.Text = string.Empty;
            return;
        }

        var remaining = _coordinator.Hako.PendingTimeRemaining;
        PendingCountdown.Text = string.Format(HakoStrings.Get("HakoAuthPendingCountdown"),
            (int)remaining.TotalMinutes, remaining.Seconds);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _coordinator?.BackFromHakoSignIn();
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e) => StartLogin(UsernameBox.Text);

    private void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        _localError = null;
        _copied = false;
        try
        {
            _ = _coordinator?.BeginHakoRegistration();
        }
        catch (HakoAuthException exception)
        {
            _localError = exception.Kind;
            RefreshState();
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        _localError = null;
        _copied = false;
        if (_coordinator is null) return;
        if (_coordinator.Hako.AuthorizationMode == HakoAuthorizationMode.Register)
        {
            try { _ = _coordinator.BeginHakoRegistration(); }
            catch (HakoAuthException exception) { _localError = exception.Kind; RefreshState(); }
        }
        else
        {
            StartLogin(UsernameBox.Text);
        }
    }

    private void FreshLoginButton_Click(object sender, RoutedEventArgs e) => StartLogin(FreshUsernameBox.Text);

    private void StartLogin(string username)
    {
        _localError = null;
        _copied = false;
        if (!HakoUsername.TryNormalize(username, out var normalizedUsername))
        {
            _localError = HakoAuthErrorKind.InvalidUsername;
            RefreshState();
            return;
        }

        UsernameBox.Text = normalizedUsername;
        FreshUsernameBox.Text = normalizedUsername;
        try
        {
            _ = _coordinator?.BeginHakoSignIn(normalizedUsername);
        }
        catch (HakoAuthException exception)
        {
            _localError = exception.Kind;
            RefreshState();
        }
    }

    private async void OpenBotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator?.Hako.BotLink is not { } botLink)
        {
            return;
        }

        var opened = false;
        try { opened = await Windows.System.Launcher.LaunchUriAsync(botLink); }
        catch (Exception) { }
        if (!opened)
        {
            PendingNotice.Message = HakoStrings.Get("HakoAuthOpenTelegramFailed");
            PendingNotice.IsOpen = true;
        }
    }

    private void CopyBotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator?.Hako.BotLink is not { } botLink)
        {
            return;
        }

        try
        {
            var data = new DataPackage();
            data.SetText(botLink.AbsoluteUri);
            Clipboard.SetContent(data);
            Clipboard.Flush();
            _copied = true;
            CopyBotButton.Content = HakoStrings.Get("HakoAuthCopiedLink");
        }
        catch (Exception)
        {
            PendingNotice.Message = HakoStrings.Get("HakoAuthCopyLinkFailed");
            PendingNotice.IsOpen = true;
        }
    }
}
