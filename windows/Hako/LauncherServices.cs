using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Hako.Core;
using Hako.Core.Instances;
using Hako.Core.Minecraft;
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
    MinecraftSkinPreviewService SkinPreview,
    ProfilePreviewOptions? PreviewOptions);

internal sealed record InstanceProfileContext(LauncherServices Services, Guid InstanceId);

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

    public static XamlRoot? Root { get; set; }

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try
        {
            if (Root is null)
            {
                return ContentDialogResult.None;
            }

            dialog.XamlRoot = Root;
            dialog.RequestedTheme = (Root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static Task<ContentDialogResult> ConfirmAsync(
        string title,
        string message,
        string primary,
        string close,
        string? secondary = null,
        bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary
        };
        if (secondary is not null)
        {
            dialog.SecondaryButtonText = secondary;
        }

        return ShowAsync(dialog);
    }

    public static Task ShowErrorAsync(string message) => ShowAsync(new ContentDialog
    {
        Title = HakoStrings.Get("ErrorTitle"),
        Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
        CloseButtonText = HakoStrings.Get("CloseButton"),
        DefaultButton = ContentDialogButton.Close
    });

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
    public static string LoaderTitle(GameInstance instance) => instance.ModLoader == InstanceModLoader.Fabric ? "Fabric" : "Vanilla";

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
