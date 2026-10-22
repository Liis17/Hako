using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Hako.Core.Instances;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Hako;

public sealed partial class BuildsPage : Page
{
    private readonly ObservableCollection<BuildCardModel> _cards = [];
    private LauncherServices? _services;
    private CoalescedRefresh? _refresh;

    public BuildsPage()
    {
        InitializeComponent();
        Cards.ItemsSource = _cards;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (LauncherServices)e.Parameter;
        _refresh = new CoalescedRefresh(DispatcherQueue, Refresh);
        _services.Store.InstancesChanged += Changed;
        _services.Store.BusyStateChanged += Changed;
        _services.Installations.PropertyChanged += Changed;
        _services.Games.RunStateChanged += Changed;
        _services.App.MinecraftSessions.PropertyChanged += Changed;
        HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
        RefreshStrings();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _refresh?.Stop();
        if (_services is { } services)
        {
            services.Store.InstancesChanged -= Changed;
            services.Store.BusyStateChanged -= Changed;
            services.Installations.PropertyChanged -= Changed;
            services.Games.RunStateChanged -= Changed;
            services.App.MinecraftSessions.PropertyChanged -= Changed;
        }

        HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
        base.OnNavigatedFrom(e);
    }

    private void Changed(object? sender, EventArgs e) => _refresh?.Request();

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e) => RefreshStrings();

    private void RefreshStrings()
    {
        BuildsTitle.Text = HakoStrings.Get("BuildsTitle");
        BuildsEmptyTitle.Text = HakoStrings.Get("BuildsEmptyTitle");
        BuildsEmptyBody.Text = HakoStrings.Get("BuildsEmptyBody");
        CreateButton.Content = HakoStrings.Get("CreateBuildButton");
        Refresh();
    }

    /// <summary>Updates cards in place: unchanged cards keep their elements and only changed properties notify.</summary>
    private void Refresh()
    {
        if (_services is not { } services)
        {
            return;
        }

        var loadError = services.Store.LoadError;
        RegistryError.IsOpen = loadError is not null;
        RegistryError.Title = loadError is null ? string.Empty : HakoStrings.Get("ErrorTitle");
        RegistryError.Message = loadError is null ? string.Empty : HakoStrings.Get(loadError.ResourceKey);

        var instances = services.Store.Instances.OrderBy(instance => instance.CreatedAt).ThenBy(instance => instance.Id).ToList();
        var progress = services.Installations.Progress;
        var ids = instances.Select(instance => instance.Id).ToHashSet();
        for (var index = _cards.Count - 1; index >= 0; index--)
        {
            if (!ids.Contains(_cards[index].Id))
            {
                _cards.RemoveAt(index);
            }
        }

        for (var index = 0; index < instances.Count; index++)
        {
            var instance = instances[index];
            var card = _cards.FirstOrDefault(item => item.Id == instance.Id);
            if (card is null)
            {
                card = new BuildCardModel(instance.Id);
                _cards.Insert(index, card);
            }
            else if (_cards.IndexOf(card) != index)
            {
                _cards.Move(_cards.IndexOf(card), index);
            }

            card.Update(services, instance, progress.GetValueOrDefault(instance.Id));
        }

        var empty = instances.Count == 0;
        EmptyPanel.Visibility = empty && loadError is null ? Visibility.Visible : Visibility.Collapsed;
        CardsScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CreateButton_Click(object sender, RoutedEventArgs e) => MainWindow.RequestCreation(App.CurrentWindow);

    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!e.Handled && sender is FrameworkElement { DataContext: BuildCardModel card })
        {
            _services?.App.OpenInstance(card.Id);
        }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is { } services && sender is FrameworkElement { Tag: BuildCardModel card })
        {
            InstancePlayLogic.Launch(services, card.Id);
        }
    }
}

public sealed class BuildCardModel(Guid id) : INotifyPropertyChanged
{
    private static readonly Brush SecondaryBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private static readonly Brush CriticalBrush = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
    private Guid? _iconRevision;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; } = id;
    public string Name { get; private set; } = "";
    public string Details { get; private set; } = "";
    public string? IconSymbol { get; private set; }
    public ImageSource? IconImage { get; private set; }
    public string Status { get; private set; } = "";
    public Brush StatusBrush { get; private set; } = SecondaryBrush;
    public Visibility StatusVisibility { get; private set; } = Visibility.Collapsed;
    public double Progress { get; private set; }
    public bool ProgressIndeterminate { get; private set; }
    public Visibility ProgressVisibility { get; private set; } = Visibility.Collapsed;
    public Visibility PlayVisibility { get; private set; } = Visibility.Collapsed;
    public bool PlayEnabled { get; private set; }
    public string PlayText { get; private set; } = "";
    public string? PlayHint { get; private set; }

    internal void Update(LauncherServices services, GameInstance instance, Hako.Core.Minecraft.InstallationProgress? progress)
    {
        Set(nameof(Name), Name, instance.Name, value => Name = value);
        Set(nameof(Details), Details, InstanceText.Details(instance), value => Details = value);
        Set(nameof(IconSymbol), IconSymbol, instance.IconSymbol, value => IconSymbol = value);
        if (_iconRevision != instance.IconRevision)
        {
            _iconRevision = instance.IconRevision;
            _ = LoadIconAsync(services, instance);
        }

        var ready = instance.State == InstanceInstallationState.Ready;
        string status;
        var brush = SecondaryBrush;
        double? fraction = null;
        var showProgress = false;
        if (ready)
        {
            var run = services.Games.GetState(instance.Id);
            status = run?.Phase switch
            {
                Hako.Core.Instances.GameRunPhase.Preparing => HakoStrings.Get("PlayPreparing"),
                Hako.Core.Instances.GameRunPhase.Running => HakoStrings.Get("GameRunning"),
                Hako.Core.Instances.GameRunPhase.Failed => LaunchErrorText(run),
                _ => ""
            };
            if (run?.Phase == Hako.Core.Instances.GameRunPhase.Failed)
            {
                brush = CriticalBrush;
            }
        }
        else if (instance.State == InstanceInstallationState.Failed)
        {
            status = InstanceText.InstallationError(instance);
            brush = CriticalBrush;
        }
        else
        {
            status = InstanceText.Status(instance, progress);
            showProgress = instance.State == InstanceInstallationState.Installing;
            fraction = InstanceText.Fraction(progress);
        }

        Set(nameof(Status), Status, status, value => Status = value);
        Set(nameof(StatusBrush), StatusBrush, brush, value => StatusBrush = value);
        Set(nameof(StatusVisibility), StatusVisibility, status.Length > 0 ? Visibility.Visible : Visibility.Collapsed, value => StatusVisibility = value);
        Set(nameof(ProgressVisibility), ProgressVisibility, showProgress ? Visibility.Visible : Visibility.Collapsed, value => ProgressVisibility = value);
        Set(nameof(ProgressIndeterminate), ProgressIndeterminate, showProgress && fraction is null, value => ProgressIndeterminate = value);
        Set(nameof(Progress), Progress, fraction ?? 0, value => Progress = value);

        var play = ready ? InstancePlayLogic.Evaluate(services, instance) : null;
        Set(nameof(PlayVisibility), PlayVisibility, ready ? Visibility.Visible : Visibility.Collapsed, value => PlayVisibility = value);
        Set(nameof(PlayEnabled), PlayEnabled, play?.Enabled == true, value => PlayEnabled = value);
        Set(nameof(PlayText), PlayText, HakoStrings.Get("PlayButton"), value => PlayText = value);
        Set(nameof(PlayHint), PlayHint, play?.Reason ?? HakoStrings.Get("PlayHint"), value => PlayHint = value);
    }

    internal static string LaunchErrorText(Hako.Core.Instances.GameRunState run)
    {
        var text = HakoStrings.Get(run.ErrorResourceKey ?? "LaunchProcessStartFailed");
        return run.ExitCode is { } code ? string.Format(text, code) : text;
    }

    private async Task LoadIconAsync(LauncherServices services, GameInstance instance)
    {
        var image = await InstanceIconSource.CreateAsync(await InstanceIconSource.ReadAsync(services.Store, instance));
        if (_iconRevision == instance.IconRevision)
        {
            IconImage = image;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconImage)));
        }
    }

    private void Set<T>(string name, T current, T value, Action<T> assign)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return;
        }

        assign(value);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
