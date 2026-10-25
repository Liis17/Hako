using Hako.Core;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Hako;

public sealed partial class MinecraftCharacterEditorView : UserControl
{
    private MinecraftCharacterEditor? _editor;
    private MinecraftSkinPreviewService? _previews;
    private int _displayedRevision = -1;
    private bool _refreshing;
    private bool _picking;
    private bool _windowVisible;
    private int _openRevision;
    private string? _inputError;
    private bool? _wide;
    private string _capeSignature = "";
    private readonly Dictionary<object, string?> _capeItems = [];

    public MinecraftCharacterEditorView()
    {
        InitializeComponent();
        PreviewView.PreviewReadinessChanged += (revision, ready) => _editor?.SetPreviewReady(revision, ready);
    }

    public event EventHandler? BackRequested;
    public event EventHandler? StateChanged;
    public bool IsSaving => _editor?.IsSaving == true;

    internal async Task OpenAsync(AppCoordinator app, MinecraftSkinPreviewService previews, bool fixture)
    {
        Close();
        var revision = _openRevision;
        _previews = previews;
        IMinecraftProfileApi api = fixture ? await MinecraftCharacterEditorFixture.CreateAsync() : previews.CreateProfileApi();
        if (_openRevision != revision) return;
        _editor = new MinecraftCharacterEditor(app, api, new MinecraftAppearanceDecoder());
        _editor.Changed += Editor_Changed;
        _editor.AppearanceConfirmed += AppearanceConfirmed;
        HakoStrings.LanguageChanged += LanguageChanged;
        Refresh();
        await _editor.OpenAsync();
    }

    internal void Close()
    {
        _openRevision++;
        if (_editor is not null)
        {
            _editor.Changed -= Editor_Changed;
            _editor.AppearanceConfirmed -= AppearanceConfirmed;
            _editor.Dispose();
            _editor = null;
        }
        HakoStrings.LanguageChanged -= LanguageChanged;
        PreviewView.SetEligible(false);
        _displayedRevision = -1;
        _capeSignature = "";
        _previews = null;
        _inputError = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void SetWindowState(bool visible, bool active)
    {
        _windowVisible = visible;
        PreviewView.SetEligible(visible && _editor is not null);
        PreviewView.SetWindowActive(active);
    }

    private void AppearanceConfirmed(MinecraftAppearanceProfile profile, MinecraftAppearanceImage image) =>
        _previews?.ConfirmAppearance(profile.Id, image);
    private void Editor_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);
    private void LanguageChanged(object? sender, EventArgs e) { _capeSignature = ""; Refresh(); }

    private void Refresh()
    {
        var editor = _editor;
        if (editor is null) return;
        _refreshing = true;
        EditorTitle.Text = HakoStrings.Get("EditCharacter");
        BackButton.Content = HakoStrings.Get("ProfileTitle");
        SaveButton.Content = HakoStrings.Get(editor.IsSaving ? "CharacterSaving" : "CharacterSave");
        SaveButton.IsEnabled = editor.CanSave && !_picking;
        BackButton.IsEnabled = !editor.IsSaving;
        SkinTitle.Text = HakoStrings.Get("CharacterSkin");
        CapeTitle.Text = HakoStrings.Get("CharacterCape");
        ImportButton.Content = HakoStrings.Get(editor.HasImportedSkin ? "CharacterReplacePng" : "CharacterChoosePng");
        ImportButton.IsEnabled = !editor.IsBusy && !editor.IsSaving && editor.Profile is not null && !_picking;
        VariantBox.IsEnabled = editor.HasImportedSkin && !editor.IsLegacy && !editor.IsBusy && !editor.IsSaving;
        VariantBox.SelectedIndex = editor.SelectedVariant == MinecraftSkinVariant.Slim ? 1 : 0;
        ClassicItem.Content = HakoStrings.Get("CharacterClassic");
        SlimItem.Content = HakoStrings.Get("CharacterSlim");
        LegacyCaption.Text = HakoStrings.Get("CharacterLegacy");
        LegacyCaption.Visibility = editor.IsLegacy ? Visibility.Visible : Visibility.Collapsed;
        NoCapesCaption.Text = HakoStrings.Get("CharacterNoCapes");
        NoCapesCaption.Visibility = editor.Profile?.Capes.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CapeList.IsEnabled = !editor.IsBusy && !editor.IsSaving;
        BusyProgress.Visibility = editor.IsBusy || editor.IsSaving ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = (_inputError ?? editor.ErrorKey) is { } key ? HakoStrings.Get(key) : "";
        StatusText.Visibility = _inputError is null && editor.ErrorKey is null ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Content = HakoStrings.Get(editor.ErrorKey == "CharacterSessionError" ? "CharacterReconnect" : "CharacterRetry");
        RetryButton.Visibility = editor.ErrorKey is not null ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = !editor.IsBusy && !editor.IsSaving;
        RefreshCapes(editor);
        if (editor.Preview is { } image && _displayedRevision != editor.PreviewRevision)
        {
            _displayedRevision = editor.PreviewRevision;
            PreviewView.SetLocalPreview(new(image.SkinRgba, image.Variant, image.CapeRgba,
                MinecraftSkinHeadComposer.Compose(image.SkinRgba)), editor.PreviewRevision);
            PreviewView.SetEligible(_windowVisible);
        }
        _refreshing = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshCapes(MinecraftCharacterEditor editor)
    {
        var signature = editor.Profile is null ? "" : string.Join('|', editor.Profile.Capes.Select(c => c.Id))
            + ":" + editor.CapeImages.Count + ":" + HakoStrings.Get("CharacterNoCape");
        if (_capeSignature != signature)
        {
            _capeSignature = signature;
            _capeItems.Clear();
            CapeList.Items.Clear();
            if (editor.Profile is not null)
            {
                AddCape(null, HakoStrings.Get("CharacterNoCape"), null);
                foreach (var cape in editor.Profile.Capes)
                    AddCape(cape.Id, string.IsNullOrWhiteSpace(cape.Alias) ? cape.Id : cape.Alias,
                        editor.CapeImages.GetValueOrDefault(cape.Id));
            }
        }
        CapeList.SelectedItem = _capeItems.FirstOrDefault(item => item.Value == editor.SelectedCapeId).Key;
    }

    private void AddCape(string? id, string title, byte[]? png)
    {
        var panel = new StackPanel { Width = 108, Spacing = 6, Padding = new Thickness(6) };
        if (png is not null)
        {
            var image = new Image { Width = 80, Height = 44 };
            panel.Children.Add(image);
            _ = SetCapeImageAsync(image, png);
        }
        else panel.Children.Add(new FontIcon { Glyph = id is null ? "\uE711" : "\uE7B8", FontSize = 24, Height = 44 });
        panel.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        _capeItems[panel] = id;
        CapeList.Items.Add(panel);
    }

    private static async Task SetCapeImageAsync(Image image, byte[] png)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
        }
        catch (Exception) { } // Choosing the tile retries its full texture through the coordinator.
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!IsSaving) BackRequested?.Invoke(this, EventArgs.Empty);
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var editor = _editor;
        if (editor is not null && await editor.SaveAsync() && ReferenceEquals(editor, _editor))
            BackRequested?.Invoke(this, EventArgs.Empty);
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_picking || _editor is not { } editor || App.MainWindowId is not { } window) return;
        _picking = true;
        _inputError = null;
        Refresh();
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window) { FileTypeFilter = { ".png" } };
            if (await picker.PickSingleFileAsync() is { } file && ReferenceEquals(editor, _editor))
                await editor.ImportAsync(file.Path);
        }
        catch (Exception) { _inputError = "CharacterInvalidPng"; }
        finally { _picking = false; Refresh(); }
    }
    private async void Variant_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && _editor is { } editor)
            await editor.SelectVariantAsync(VariantBox.SelectedIndex == 1 ? MinecraftSkinVariant.Slim : MinecraftSkinVariant.Classic);
    }
    private async void Cape_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_editor is { } editor && _capeItems.TryGetValue(e.ClickedItem, out var id)) await editor.SelectCapeAsync(id);
    }
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is not { } editor) return;
        if (editor.ErrorKey == "CharacterSessionError") await editor.ReconnectAsync();
        else if (editor.Profile is null) await editor.OpenAsync();
        else if (editor.ErrorKey is "CharacterSaveError" or "CharacterSaveUnconfirmed") await editor.SaveAsync();
        else await editor.RetryPreviewAsync();
    }
    private void Editor_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 700;
        if (_wide == wide) return;
        _wide = wide;
        EditorLayout.ColumnDefinitions[1].Width = wide ? new GridLength(360) : new GridLength(0);
        Grid.SetColumn(EditorControls, wide ? 1 : 0);
        Grid.SetRow(EditorControls, wide ? 0 : 1);
        PreviewView.Height = wide ? 480 : 360;
    }
}
