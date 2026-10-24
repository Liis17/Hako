using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
using Hako.Core.Playtime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Hako;

/// <summary>App-lifetime services shared by the launcher pages (the Windows counterpart of HakoServices).</summary>
internal sealed record LauncherServices(
    AppCoordinator App,
    UserSettingsService Settings,
    InstanceStore Store,
    InstallationCoordinator Installations,
    GameLaunchCoordinator Games,
    MojangClient Mojang,
    FabricClient Fabric,
    ModrinthClient Modrinth,
    ModpackInstaller Modpacks,
    ModpackCatalog ModpackCatalog,
    InstanceContent Content,
    InstanceContentController ContentController,
    InstanceWorlds Worlds,
    InstanceScreenshots Screenshots,
    InstanceBackup Backups,
    WorldBackup WorldBackups,
    RestoreArchive Restores,
    HttpClient? PreviewModrinthHttpClient,
    MinecraftSkinPreviewService SkinPreview,
    ProfilePreviewOptions? PreviewOptions,
    PlaytimeCoordinator Playtime);

internal enum InstanceProfileSection { Mods, ResourcePacks, Worlds, Screenshots, Settings }

internal sealed record InstanceProfileContext(
    LauncherServices Services,
    Guid InstanceId,
    InstanceProfileSection? InitialSection = null);

/// <summary>A page with unsaved input asks before the window navigates away or closes.</summary>
internal interface INavigationGuard
{
    Task<bool> ConfirmLeaveAsync();
}

/// <summary>
/// Collapses notifications raised on Core worker threads (often while Core holds its locks) into one
/// refresh on the UI thread. The handler only enqueues and never calls back into Core synchronously.
/// </summary>
internal sealed class CoalescedRefresh(DispatcherQueue dispatcher, Action refresh)
{
    private int _pending;
    private bool _stopped;

    public void Request()
    {
        if (Volatile.Read(ref _stopped) || Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        if (!dispatcher.TryEnqueue(Run))
        {
            Volatile.Write(ref _pending, 0);
        }
    }

    public void Stop() => Volatile.Write(ref _stopped, true);

    private void Run()
    {
        Volatile.Write(ref _pending, 0);
        if (!Volatile.Read(ref _stopped))
        {
            refresh();
        }
    }
}

/// <summary>Shows one ContentDialog at a time: WinUI allows a single open dialog per XamlRoot.</summary>
internal static class HakoDialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object WindowSync = new();
    private static CancellationTokenSource _windowLifetime = new();
    private static CancellationTokenSource _contentConfirmationLifetime = new();
    private static DispatcherQueue? _dispatcher;
    private static XamlRoot? _root;
    private static bool _windowClosed;
    private static bool _windowClosing;

    public static XamlRoot? Root
    {
        get { lock (WindowSync) return _root; }
        set { lock (WindowSync) _root = value; }
    }

    public static void AttachWindow(DispatcherQueue dispatcher, XamlRoot root)
    {
        lock (WindowSync)
        {
            _dispatcher = dispatcher;
            _root = root;
            _windowClosed = false;
            _windowClosing = false;
        }
    }

    public static void BeginWindowClose()
    {
        CancellationTokenSource pending;
        lock (WindowSync)
        {
            _windowClosing = true;
            pending = _contentConfirmationLifetime;
            _contentConfirmationLifetime = new CancellationTokenSource();
        }

        pending.Cancel();
    }

    public static void EndWindowClose()
    {
        CancellationTokenSource pending;
        lock (WindowSync)
        {
            if (_windowClosed) return;
            _windowClosing = false;
            pending = _contentConfirmationLifetime;
            _contentConfirmationLifetime = new CancellationTokenSource();
        }

        pending.Cancel();
    }

    public static void CloseWindow()
    {
        CancellationTokenSource[] pending;
        lock (WindowSync)
        {
            _windowClosed = true;
            _root = null;
            _dispatcher = null;
            pending = [_windowLifetime, _contentConfirmationLifetime];
            _windowLifetime = new CancellationTokenSource();
            _contentConfirmationLifetime = new CancellationTokenSource();
        }

        foreach (var cancellation in pending) cancellation.Cancel();
    }

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, Action<ContentDialog>? refresh = null)
    {
        EventHandler? languageChanged = null;
        try
        {
            return await ShowAsync(() =>
            {
                if (refresh is not null)
                {
                    refresh(dialog);
                    languageChanged = (_, _) => refresh(dialog);
                    HakoStrings.LanguageChanged += languageChanged;
                }
                return dialog;
            }, CancellationToken.None);
        }
        finally
        {
            if (languageChanged is not null) HakoStrings.LanguageChanged -= languageChanged;
        }
    }

    private static async Task<ContentDialogResult> ShowAsync(
        Func<ContentDialog> createDialog,
        CancellationToken cancellationToken,
        Action<ContentDialog>? opened = null)
    {
        CancellationToken windowToken;
        lock (WindowSync)
        {
            if (_windowClosed) return ContentDialogResult.None;
            windowToken = _windowLifetime.Token;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, windowToken);
        cancellationToken = lifetime.Token;
        try
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ContentDialogResult.None;
        }

        try
        {
            DispatcherQueue? dispatcher;
            lock (WindowSync) dispatcher = _dispatcher;
            if (dispatcher is null)
            {
                return ContentDialogResult.None;
            }

            var completion = new TaskCompletionSource<ContentDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dialogSync = new object();
            ContentDialog? activeDialog = null;
            void CancelOnUiThread()
            {
                ContentDialog? dialog;
                lock (dialogSync) dialog = activeDialog;
                if (dialog is null)
                {
                    completion.TrySetResult(ContentDialogResult.None);
                    return;
                }

                HideDialog(dialog);
            }

            using var registration = cancellationToken.Register(() =>
            {
                if (dispatcher.HasThreadAccess) CancelOnUiThread();
                else if (!dispatcher.TryEnqueue(CancelOnUiThread)) completion.TrySetResult(ContentDialogResult.None);
            });

            if (dispatcher.HasThreadAccess)
            {
                return await ShowOnUiThreadAsync(createDialog, cancellationToken, dialog =>
                {
                    lock (dialogSync) activeDialog = dialog;
                }, opened);
            }

            if (!dispatcher.TryEnqueue(() => _ = CompleteOnUiThreadAsync(createDialog, cancellationToken,
                    dialog =>
                    {
                        lock (dialogSync) activeDialog = dialog;
                    }, completion, opened)))
            {
                return ContentDialogResult.None;
            }

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<(bool Cancelled, T? Value)> ShowProgressAsync<T>(
        ContentDialog dialog,
        Func<CancellationToken, Task<T>> operation,
        Action<ContentDialog>? refresh = null)
    {
        using var operationCancellation = new CancellationTokenSource();
        var operationCompletion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationStarted = false;
        var operationFinished = 0;
        var userCancelled = 0;
        EventHandler? languageChanged = null;
        dialog.CloseButtonClick += (_, _) => Interlocked.Exchange(ref userCancelled, 1);
        dialog.Closing += (_, _) =>
        {
            if (Volatile.Read(ref operationFinished) == 0) Interlocked.Exchange(ref userCancelled, 1);
        };

        async Task RunOperationAsync(ContentDialog activeDialog)
        {
            try
            {
                var value = await operation(operationCancellation.Token);
                Volatile.Write(ref operationFinished, 1);
                operationCompletion.TrySetResult(value);
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref operationFinished, 1);
                operationCompletion.TrySetCanceled(operationCancellation.Token);
            }
            catch (Exception exception)
            {
                Volatile.Write(ref operationFinished, 1);
                operationCompletion.TrySetException(exception);
            }
            finally
            {
                DispatcherQueue? dispatcher;
                lock (WindowSync) dispatcher = _dispatcher;
                if (dispatcher?.HasThreadAccess == true) HideDialog(activeDialog);
                else dispatcher?.TryEnqueue(() => HideDialog(activeDialog));
            }
        }

        try
        {
            await ShowAsync(() =>
            {
                refresh?.Invoke(dialog);
                if (refresh is not null)
                {
                    languageChanged = (_, _) => refresh(dialog);
                    HakoStrings.LanguageChanged += languageChanged;
                }
                return dialog;
            }, CancellationToken.None, activeDialog =>
            {
                operationStarted = true;
                _ = RunOperationAsync(activeDialog);
            });

            // The operation never starts when the dialog failed to open, so there is nothing to wait for.
            if (!operationStarted) return (true, default);

            if (Volatile.Read(ref userCancelled) != 0)
            {
                operationCancellation.Cancel();
                try { return (true, await operationCompletion.Task); }
                catch (OperationCanceledException) { }
                return (true, default);
            }

            try
            {
                return (false, await operationCompletion.Task);
            }
            catch (OperationCanceledException)
            {
                return (true, default);
            }
        }
        finally
        {
            if (languageChanged is not null) HakoStrings.LanguageChanged -= languageChanged;
        }
    }

    public static async Task<ContentConfirmationResult> ConfirmContentAsync(
        ContentConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        CancellationToken windowToken;
        CancellationToken contentToken;
        lock (WindowSync)
        {
            if (_windowClosed || _windowClosing) return new ContentConfirmationResult(ContentConfirmationChoice.Cancel);
            windowToken = _windowLifetime.Token;
            contentToken = _contentConfirmationLifetime.Token;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, windowToken, contentToken);
        ContentConfirmationPresentation? presentation = null;
        EventHandler? languageChanged = null;
        try
        {
            var result = await ShowAsync(() =>
            {
                presentation = new ContentConfirmationPresentation(request);
                languageChanged = (_, _) =>
                {
                    DispatcherQueue? dispatcher;
                    lock (WindowSync) dispatcher = _dispatcher;
                    if (dispatcher?.HasThreadAccess == true) presentation.Refresh();
                    else dispatcher?.TryEnqueue(presentation.Refresh);
                };
                HakoStrings.LanguageChanged += languageChanged;
                presentation.Refresh();
                return presentation.Dialog;
            }, linked.Token).ConfigureAwait(false);

            return result switch
            {
                ContentDialogResult.Primary => new ContentConfirmationResult(
                    ContentConfirmationChoice.Primary, presentation?.ChosenFilename),
                ContentDialogResult.Secondary => new ContentConfirmationResult(ContentConfirmationChoice.Alternative),
                _ => new ContentConfirmationResult(ContentConfirmationChoice.Cancel)
            };
        }
        catch (OperationCanceledException)
        {
            return new ContentConfirmationResult(ContentConfirmationChoice.Cancel);
        }
        finally
        {
            if (languageChanged is not null) HakoStrings.LanguageChanged -= languageChanged;
        }
    }

    private static async Task CompleteOnUiThreadAsync(Func<ContentDialog> createDialog,
        CancellationToken cancellationToken, Action<ContentDialog?> setDialog,
        TaskCompletionSource<ContentDialogResult> completion, Action<ContentDialog>? opened)
    {
        try
        {
            completion.TrySetResult(await ShowOnUiThreadAsync(createDialog, cancellationToken, setDialog, opened));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static async Task<ContentDialogResult> ShowOnUiThreadAsync(
        Func<ContentDialog> createDialog, CancellationToken cancellationToken, Action<ContentDialog?> setDialog,
        Action<ContentDialog>? opened = null)
    {
        if (cancellationToken.IsCancellationRequested) return ContentDialogResult.None;
        var root = Root;
        if (root is null) return ContentDialogResult.None;

        var dialog = createDialog();
        dialog.XamlRoot = root;
        dialog.RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
        setDialog(dialog);
        if (opened is not null) dialog.Opened += (_, _) => opened(dialog);

        try
        {
            if (cancellationToken.IsCancellationRequested) return ContentDialogResult.None;
            return await dialog.ShowAsync();
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested || Root is null)
        {
            return ContentDialogResult.None;
        }
        finally
        {
            setDialog(null);
        }
    }

    private static void HideDialog(ContentDialog dialog)
    {
        try { dialog.Hide(); }
        catch (InvalidOperationException) { }
    }

    private sealed class ContentConfirmationPresentation
    {
        private readonly ContentConfirmationRequest _request;
        private readonly TextBlock _message;
        private readonly List<(TextBlock Title, TextBlock? Note, ContentConfirmationProject Project)> _projects;
        private readonly List<(RadioButton Button, ContentConfirmationFile File, TextBlock Note)> _files;

        public ContentConfirmationPresentation(ContentConfirmationRequest request)
        {
            _request = request;
            _message = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var rows = new StackPanel { Spacing = 10 };
            _projects = [];
            _files = [];
            foreach (var project in request.Projects)
            {
                var title = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                TextBlock? note = project.NoteCode is null ? null : new TextBlock
                {
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                };
                var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(title);
                if (note is not null) text.Children.Add(note);
                var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(36) }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } }, ColumnSpacing = 10 };
                if (project.IconUrl is { IsAbsoluteUri: true, Scheme: "https" } iconUrl)
                {
                    row.Children.Add(new Image
                    {
                        Width = 32,
                        Height = 32,
                        Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(iconUrl),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                else
                {
                    row.Children.Add(new FontIcon { Glyph = "\uE71D", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
                }
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                rows.Children.Add(row);
                _projects.Add((title, note, project));
            }

            var body = new StackPanel { Spacing = 12 };
            body.Children.Add(_message);
            if (request.Files is { Count: > 0 } files)
            {
                var fileRows = new StackPanel { Spacing = 8 };
                var groupName = "ContentFileChoice" + Guid.NewGuid().ToString("N");
                foreach (var file in files)
                {
                    var title = new TextBlock { Text = file.Filename, TextWrapping = TextWrapping.Wrap };
                    var note = new TextBlock
                    {
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    };
                    var text = new StackPanel { Spacing = 2, Children = { title, note } };
                    var option = new RadioButton
                    {
                        Content = text,
                        GroupName = groupName,
                        IsChecked = _files.Count == 0,
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    fileRows.Children.Add(option);
                    _files.Add((option, file, note));
                }

                body.Children.Add(new ScrollViewer
                {
                    MaxHeight = 220,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = fileRows
                });
            }

            if (rows.Children.Count > 0)
            {
                body.Children.Add(new ScrollViewer { MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows });
            }

            Dialog = new ContentDialog
            {
                Content = body,
                PrimaryButtonText = request.PrimaryActionAvailable ? HakoStrings.Get(ContentResourceKeys.ConfirmationPrimary(request.Code)) : "",
                SecondaryButtonText = request.AlternativeActionAvailable ? HakoStrings.Get(ContentResourceKeys.ConfirmationAlternative(request.Code)) : "",
                CloseButtonText = HakoStrings.Get("CancelButton"),
                DefaultButton = request.Destructive ? ContentDialogButton.Close : ContentDialogButton.Primary
            };
            Dialog.PrimaryButtonClick += (_, _) => ChosenFilename = SelectedFilename;
        }

        public ContentDialog Dialog { get; }
        public string? ChosenFilename { get; private set; }

        private string? SelectedFilename => _files.FirstOrDefault(item => item.Button.IsChecked == true).File?.Filename;

        public void Refresh()
        {
            Dialog.Title = HakoStrings.Get(ContentResourceKeys.ConfirmationTitle(_request.Code));
            _message.Text = FormatMessage(ContentResourceKeys.ConfirmationMessage(_request.Code), _request.Arguments,
                _request.Code switch
                {
                    ContentConfirmationCode.Dependencies => ["projectTitle", "instanceName"],
                    ContentConfirmationCode.IncompatibleContent => ["projectTitle", "instanceName"],
                    ContentConfirmationCode.ReplaceExisting => ["filename", "instanceName"],
                    ContentConfirmationCode.DeleteContent => ["filename", "instanceName"],
                    _ => []
                });
            Dialog.PrimaryButtonText = _request.PrimaryActionAvailable ? HakoStrings.Get(ContentResourceKeys.ConfirmationPrimary(_request.Code)) : "";
            Dialog.SecondaryButtonText = _request.AlternativeActionAvailable ? HakoStrings.Get(ContentResourceKeys.ConfirmationAlternative(_request.Code)) : "";
            Dialog.CloseButtonText = HakoStrings.Get("CancelButton");
            foreach (var (title, note, project) in _projects)
            {
                title.Text = project.Title;
                if (note is not null && project.NoteCode is { } code)
                {
                    note.Text = FormatMessage(ContentResourceKeys.ConfirmationNote(code), project.NoteArguments ?? EmptyArguments,
                        code switch
                        {
                            ContentConfirmationNoteCode.ProjectLimitReached => ["limit"],
                            ContentConfirmationNoteCode.FabricLoaderRequirement or ContentConfirmationNoteCode.JavaRequirement => ["required", "current"],
                            ContentConfirmationNoteCode.IncompatibleWith => ["projectTitle"],
                            _ => []
                        });
                }
            }

            foreach (var (_, file, note) in _files)
                note.Text = HakoStrings.Get(file.Required ? "ContentConfirmationFileRequired" : "ContentConfirmationFileOptional");
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyArguments = new Dictionary<string, string>();
    }

    private static string FormatMessage(string key, IReadOnlyDictionary<string, string> arguments, string[] names)
    {
        var template = HakoStrings.Get(key);
        if (names.Length == 0) return template;
        try { return string.Format(template, names.Select(name => arguments.GetValueOrDefault(name, "")).ToArray()); }
        catch (FormatException) { return template; }
    }

    /// <summary>Asks whether to leave a page whose changed draft is invalid and will not be saved.</summary>
    public static async Task<bool> ConfirmLeaveInvalidDraftAsync() =>
        await ConfirmAsync(
            HakoStrings.Get("InvalidDraftTitle"),
            HakoStrings.Get("InvalidDraftMessage"),
            HakoStrings.Get("InvalidDraftLeave"),
            HakoStrings.Get("InvalidDraftStay"),
            destructive: true) == ContentDialogResult.Primary;

    public static Task<ContentDialogResult> ConfirmAsync(
        string title,
        string message,
        string primary,
        string close,
        string? secondary = null,
        bool destructive = false)
    {
        return ShowAsync(() =>
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = primary,
                CloseButtonText = close,
                DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary
            };
            if (secondary is not null) dialog.SecondaryButtonText = secondary;
            return dialog;
        }, CancellationToken.None);
    }

    public static Task ShowErrorAsync(string message) => ShowAsync(() => new ContentDialog
    {
        Title = HakoStrings.Get("ErrorTitle"),
        Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
        CloseButtonText = HakoStrings.Get("CloseButton"),
        DefaultButton = ContentDialogButton.Close
    }, CancellationToken.None);

    /// <summary>
    /// Shared step for operations that move files to the Recycle Bin: items the bin can't take (for example on a
    /// volume without one) are deleted permanently only after the user confirms. Returns the paths still left.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ConfirmPermanentDeleteAsync(
        RecycleBin recycleBin,
        RecycleBinResult result,
        string allowedRoot)
    {
        var notRecyclable = result.Items
            .Where(item => item.Status == RecycleBinItemStatus.NotRecyclable)
            .Select(item => item.Path)
            .ToList();
        if (notRecyclable.Count == 0)
        {
            return [];
        }

        var names = string.Join("\n", notRecyclable.Select(Path.GetFileName));
        var answer = await ConfirmAsync(
            HakoStrings.Get("PermanentDeleteTitle"),
            string.Format(HakoStrings.Get("PermanentDeleteMessage"), names),
            HakoStrings.Get("PermanentDeleteConfirm"),
            HakoStrings.Get("CancelButton"),
            destructive: true);
        if (answer != ContentDialogResult.Primary)
        {
            return notRecyclable;
        }

        await Task.Run(() => recycleBin.PermanentDelete(notRecyclable, allowedRoot));
        return [];
    }
}

internal static class InstanceIconSource
{
    /// <summary>Reads icon.png off the UI thread so the image never holds the file (rename and delete stay possible).</summary>
    public static Task<byte[]?> ReadAsync(InstanceStore store, GameInstance instance) => Task.Run(() =>
    {
        if (!string.IsNullOrEmpty(instance.IconSymbol))
        {
            return null;
        }

        try
        {
            var path = InstanceStorage.ContainedPath("icon.png", store.GetInstanceDirectory(instance.Id), allowBaseReparsePoint: true);
            var info = new FileInfo(path);
            return info.Exists && info.Length is > 0 and <= 4 * 1024 * 1024 ? File.ReadAllBytes(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    });

    public static async Task<ImageSource?> CreateAsync(byte[]? data)
    {
        if (data is null)
        {
            return null;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal static class InstanceText
{
    public static string LoaderTitle(GameInstance instance) => instance.ModLoader == InstanceModLoader.Fabric
        ? $"Fabric {instance.FabricConfiguration?.LoaderVersion}"
        : "Vanilla";

    public static string Details(GameInstance instance) =>
        string.Format(HakoStrings.Get("InstanceDetailsFormat"), instance.VersionID, LoaderTitle(instance));

    public static string Error(Exception exception) => HakoStrings.Get(InstallationResourceKeys.ErrorResourceKey(exception));

    public static string InstallationError(GameInstance instance) => instance.ErrorCode is { } code
        ? HakoStrings.Get(InstallationResourceKeys.ErrorResourceKey(code))
        : HakoStrings.Get("InstallationErrorUnexpected");

    public static string Status(GameInstance instance, InstallationProgress? progress)
    {
        if (instance.State == InstanceInstallationState.Installing && progress is not null)
        {
            var stage = HakoStrings.Get(progress.Stage.ResourceKey());
            return progress.TotalBytes > 0 ? $"{stage} · {Percent(progress)}%" : stage;
        }

        return HakoStrings.Get(instance.State.ResourceKey());
    }

    public static double? Fraction(InstallationProgress? progress) => progress is { TotalBytes: > 0 } value
        ? Math.Clamp((double)value.CompletedBytes / value.TotalBytes, 0, 1)
        : null;

    private static int Percent(InstallationProgress progress) => (int)Math.Floor((Fraction(progress) ?? 0) * 100);
}

internal static class FieldHeader
{
    /// <summary>A header in the UI font for text boxes whose own text is monospaced.</summary>
    public static TextBlock Create(string text) => new()
    {
        Text = text,
        FontFamily = (FontFamily)Application.Current.Resources["ContentControlThemeFontFamily"]
    };
}
