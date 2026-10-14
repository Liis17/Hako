using Hako.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace Hako;

public sealed partial class LoginPage : Page
{
    private AppCoordinator? _coordinator;
    private bool _copied;
    private string? _noticeKey;

    public LoginPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _coordinator = (AppCoordinator)e.Parameter;
        _coordinator.PropertyChanged += Coordinator_PropertyChanged;
        HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
        RefreshStrings();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_coordinator is not null)
        {
            _coordinator.PropertyChanged -= Coordinator_PropertyChanged;
        }

        HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
        base.OnNavigatedFrom(e);
    }

    private void Coordinator_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppCoordinator.SignInPhase) or nameof(AppCoordinator.CurrentDeviceCode) or nameof(AppCoordinator.LoginError))
        {
            RefreshState();
        }
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e) => RefreshStrings();

    private void RefreshStrings()
    {
        BackButton.Content = HakoStrings.Get("BackButton");
        LoginTitle.Text = HakoStrings.Get("LoginTitle");
        LoginInstructions.Text = HakoStrings.Get("LoginInstructions");
        RequestingText.Text = HakoStrings.Get("RequestingCode");
        WaitingText.Text = HakoStrings.Get("WaitingForCode");
        OpenVerificationButton.Content = HakoStrings.Get("OpenVerificationButton");
        SigningInText.Text = HakoStrings.Get("SigningIn");
        RetryButton.Content = HakoStrings.Get("RetryButton");
        RefreshState();
    }

    private void RefreshState()
    {
        if (_coordinator is null)
        {
            return;
        }

        RequestingPanel.Visibility = _coordinator.SignInPhase == SignInPhase.RequestingCode ? Visibility.Visible : Visibility.Collapsed;
        WaitingPanel.Visibility = _coordinator.SignInPhase == SignInPhase.WaitingForCode ? Visibility.Visible : Visibility.Collapsed;
        SigningInPanel.Visibility = _coordinator.SignInPhase == SignInPhase.SigningIn ? Visibility.Visible : Visibility.Collapsed;
        FailedPanel.Visibility = _coordinator.SignInPhase == SignInPhase.Failed ? Visibility.Visible : Visibility.Collapsed;

        var code = _coordinator.CurrentDeviceCode;
        UserCodeText.Text = code?.UserCode ?? string.Empty;
        VerificationUriText.Text = code?.VerificationUri.AbsoluteUri ?? string.Empty;
        CopyCodeButton.Content = HakoStrings.Get(_copied ? "CopiedCodeButton" : "CopyCodeButton");
        FailureText.Text = _coordinator.LoginError is { } error
            ? HakoStrings.Get(error.ResourceKey())
            : string.Empty;
        LoginNotice.IsOpen = _noticeKey is not null;
        if (_noticeKey is not null)
        {
            LoginNotice.Message = HakoStrings.Get(_noticeKey);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _coordinator?.BackFromSignIn();
    }

    private async void OpenVerificationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator?.CurrentDeviceCode is not { } code)
        {
            return;
        }

        _noticeKey = null;
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(code.VerificationUri))
            {
                _noticeKey = "OpenVerificationFailed";
            }
        }
        catch (Exception)
        {
            _noticeKey = "OpenVerificationFailed";
        }

        RefreshState();
    }

    private void CopyCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator?.CurrentDeviceCode is not { } code)
        {
            return;
        }

        _noticeKey = null;
        var data = new DataPackage();
        data.SetText(code.UserCode);
        try
        {
            Clipboard.SetContent(data);
            Clipboard.Flush();
            _copied = true;
        }
        catch (COMException)
        {
            _copied = false;
            _noticeKey = "CopyCodeFailed";
        }

        RefreshState();
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        _copied = false;
        _noticeKey = null;
        if (_coordinator is not null)
        {
            _ = _coordinator.RetrySignIn();
        }
    }
}
