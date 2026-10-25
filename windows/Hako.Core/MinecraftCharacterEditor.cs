using System.Net;

namespace Hako.Core;

/// <summary>One screen's draft. Call from one synchronization context; account changes cancel all work.</summary>
public sealed class MinecraftCharacterEditor : IDisposable
{
    private readonly AppCoordinator _app;
    private readonly IMinecraftProfileApi _api;
    private readonly IMinecraftAppearanceDecoder _decoder;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly long _generation;
    private readonly string? _profileId;
    private MinecraftAppearanceImage? _serverImage;
    private MinecraftSkinUpload? _upload;
    private NormalizedMinecraftSkin? _draftSkin;
    private int _previewRevision;
    private bool _disposed;

    public MinecraftCharacterEditor(AppCoordinator app, IMinecraftProfileApi api, IMinecraftAppearanceDecoder decoder)
    {
        _app = app;
        _api = api;
        _decoder = decoder;
        var state = app.CaptureAccountState();
        _generation = state.Generation;
        _profileId = state.Account?.MinecraftProfileId;
        _app.AccountGenerationChanged += AccountChanged;
    }

    public event EventHandler? Changed;
    public event Action<MinecraftAppearanceProfile, MinecraftAppearanceImage>? AppearanceConfirmed;
    public MinecraftAppearanceProfile? Profile { get; private set; }
    public MinecraftAppearanceImage? Preview { get; private set; }
    public IReadOnlyDictionary<string, byte[]> CapeImages => _capeImages;
    private readonly Dictionary<string, byte[]> _capeImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _capePixels = new(StringComparer.Ordinal);
    public string? SelectedCapeId { get; private set; }
    public MinecraftSkinVariant SelectedVariant { get; private set; }
    public bool IsLegacy => _upload?.IsLegacy == true;
    public bool HasImportedSkin => _upload is not null;
    public bool IsBusy { get; private set; }
    public bool IsSaving { get; private set; }
    public bool PreviewReady { get; private set; }
    public int PreviewRevision => _previewRevision;
    public string? ErrorKey { get; private set; }
    public bool HasChanges => SkinChanged || (Profile is not null && SelectedCapeId != Profile.ActiveCape?.Id);
    public bool CanSave => Current && !IsBusy && !IsSaving && Profile is not null && PreviewReady && HasChanges;
    private bool Current => !_disposed && !_lifetime.IsCancellationRequested && _app.CaptureAccountState().Generation == _generation;
    private bool SkinChanged => _draftSkin is not null && (_serverImage is null
        || _draftSkin.Variant != _serverImage.Variant || !_draftSkin.Rgba.AsSpan().SequenceEqual(_serverImage.SkinRgba));

    public async Task OpenAsync()
    {
        if (IsBusy || IsSaving || !Current) return;
        IsBusy = true;
        ErrorKey = null;
        Notify();
        try
        {
            var profile = await ReadProfileAsync();
            var image = await ReadImageAsync(profile);
            CheckCurrent();
            Profile = profile;
            _serverImage = image;
            _upload = null;
            _draftSkin = null;
            SelectedVariant = image.Variant;
            SelectedCapeId = profile.ActiveCape?.Id;
            AppearanceConfirmed?.Invoke(profile, image);
            await UpdatePreviewAsync();
            // A bad optional thumbnail does not prevent choosing another cape.
            foreach (var cape in profile.Capes)
            {
                try { await LoadCapeAsync(cape); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }
            }
        }
        catch (OperationCanceledException) when (!Current) { }
        catch (Exception error) { if (Current) ErrorKey = ErrorResource(error, "CharacterProfileError"); }
        finally { IsBusy = false; Notify(); }
    }

    public async Task ImportAsync(string path)
    {
        if (IsBusy || IsSaving || !Current || Profile is null) return;
        IsBusy = true;
        ErrorKey = null;
        Notify();
        try
        {
            var upload = await Task.Run(() => MinecraftSkinUpload.ReadAsync(path, _lifetime.Token), _lifetime.Token);
            var variant = upload.IsLegacy ? MinecraftSkinVariant.Classic : SelectedVariant;
            var skin = await _decoder.DecodeSkinAsync(upload.Png, variant, _lifetime.Token);
            CheckCurrent();
            _upload = upload;
            _draftSkin = skin;
            SelectedVariant = variant;
            await UpdatePreviewAsync();
        }
        catch (OperationCanceledException) when (!Current) { }
        catch (Exception) { if (Current) ErrorKey = "CharacterInvalidPng"; }
        finally { IsBusy = false; Notify(); }
    }

    public async Task SelectVariantAsync(MinecraftSkinVariant variant)
    {
        if (IsBusy || IsSaving || !Current || _draftSkin is null || IsLegacy) return;
        SelectedVariant = variant;
        _draftSkin = _draftSkin with { Variant = variant };
        await PreparePreviewAsync();
    }

    public async Task SelectCapeAsync(string? capeId)
    {
        if (IsBusy || IsSaving || !Current || Profile is null
            || (capeId is not null && !Profile.Capes.Any(c => c.Id == capeId))) return;
        SelectedCapeId = capeId;
        await PreparePreviewAsync();
    }

    public async Task RetryPreviewAsync() => await PreparePreviewAsync();

    private async Task PreparePreviewAsync()
    {
        if (!Current || IsBusy || IsSaving) return;
        IsBusy = true;
        ErrorKey = null;
        Notify();
        try { await UpdatePreviewAsync(); }
        catch (OperationCanceledException) when (!Current) { }
        catch (Exception) { if (Current) ErrorKey = "CharacterPreviewError"; }
        finally { IsBusy = false; Notify(); }
    }

    private async Task UpdatePreviewAsync()
    {
        PreviewReady = false;
        Preview = null;
        var revision = ++_previewRevision;
        Notify();
        if (_serverImage is null) return;
        byte[]? cape = null;
        if (SelectedCapeId is { } id)
        {
            var selected = Profile!.Capes.First(c => c.Id == id);
            await LoadCapeAsync(selected);
            cape = _capePixels[id];
        }
        CheckCurrent();
        if (revision != _previewRevision) return;
        Preview = new(_draftSkin?.Rgba ?? _serverImage.SkinRgba, SelectedVariant, cape);
        Notify();
    }

    public void SetPreviewReady(int revision, bool ready)
    {
        if (!Current || revision != _previewRevision) return;
        PreviewReady = ready;
        if (!ready) ErrorKey = "CharacterPreviewError";
        Notify();
    }

    public async Task ReconnectAsync()
    {
        if (!Current || IsBusy || IsSaving) return;
        IsBusy = true;
        ErrorKey = null;
        Notify();
        try
        {
            await _app.MinecraftSessions.RefreshAsync(_lifetime.Token).WaitAsync(_lifetime.Token);
            CheckCurrent();
        }
        catch (OperationCanceledException) when (!Current) { }
        catch (Exception) { if (Current) ErrorKey = "CharacterSessionError"; }
        finally { IsBusy = false; Notify(); }
        if (Current && ErrorKey is null && Profile is null) await OpenAsync();
    }

    public async Task<bool> SaveAsync()
    {
        if (!CanSave) return false;
        IsSaving = true;
        ErrorKey = null;
        Notify();
        var success = false;
        try
        {
            // Reconcile first: a previous request may have succeeded despite a lost response.
            await ReconcileAsync();
            if (SkinChanged && _upload is { } upload)
            {
                await WriteAndConfirmAsync(() => AuthorizedAsync(async (token, ct) =>
                {
                    await _api.UploadSkinAsync(token, upload.Png, SelectedVariant, ct);
                    return true;
                }), () => !SkinChanged);
                _upload = null;
                _draftSkin = null;
            }
            if (SelectedCapeId != Profile!.ActiveCape?.Id)
                await WriteAndConfirmAsync(() => AuthorizedAsync(async (token, ct) =>
                {
                    await _api.SetCapeAsync(token, SelectedCapeId, ct);
                    return true;
                }), () => SelectedCapeId == Profile!.ActiveCape?.Id);
            CheckCurrent();
            success = !HasChanges;
        }
        catch (OperationCanceledException) when (!Current) { }
        catch (Exception error) { if (Current) ErrorKey = ErrorResource(error, "CharacterSaveError"); }
        finally { IsSaving = false; Notify(); }
        return success;
    }

    private async Task WriteAndConfirmAsync(Func<Task<bool>> write, Func<bool> confirmed)
    {
        Exception? failure = null;
        try { await write(); }
        catch (OperationCanceledException) when (!Current) { throw; }
        catch (Exception error) { failure = error; }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await ReconcileAsync();
            if (confirmed()) return;
        }
        throw failure ?? new InvalidOperationException("CharacterSaveUnconfirmed");
    }

    private async Task ReconcileAsync()
    {
        var profile = await ReadProfileAsync();
        var image = await ReadImageAsync(profile);
        CheckCurrent();
        Profile = profile;
        _serverImage = image;
        AppearanceConfirmed?.Invoke(profile, image);
    }

    private async Task<MinecraftAppearanceProfile> ReadProfileAsync()
    {
        var profile = await AuthorizedAsync((token, ct) => _api.ProfileAsync(token, ct));
        if (!Guid.TryParse(profile.Id, out var actual) || !Guid.TryParse(_profileId, out var expected) || actual != expected)
            throw new InvalidDataException("CharacterProfileError");
        return profile;
    }

    private async Task<MinecraftAppearanceImage> ReadImageAsync(MinecraftAppearanceProfile profile)
    {
        var skin = profile.ActiveSkin ?? throw new InvalidDataException("CharacterProfileError");
        var bytes = await _api.TextureAsync(skin.Url, _lifetime.Token);
        var normalized = await _decoder.DecodeSkinAsync(bytes,
            skin.Variant == "SLIM" ? MinecraftSkinVariant.Slim : MinecraftSkinVariant.Classic, _lifetime.Token);
        byte[]? cape = null;
        if (profile.ActiveCape is { } active)
        {
            await LoadCapeAsync(active);
            cape = _capePixels[active.Id];
        }
        CheckCurrent();
        return new(normalized.Rgba, normalized.Variant, cape);
    }

    private async Task LoadCapeAsync(MinecraftAppearanceCape cape)
    {
        if (_capePixels.ContainsKey(cape.Id)) return;
        var bytes = await _api.TextureAsync(cape.Url, _lifetime.Token);
        var pixels = await _decoder.DecodeCapeAsync(bytes, _lifetime.Token);
        CheckCurrent();
        _capeImages[cape.Id] = bytes;
        _capePixels[cape.Id] = pixels;
    }

    private async Task<T> AuthorizedAsync<T>(Func<string, CancellationToken, Task<T>> operation)
    {
        CheckCurrent();
        var account = await _app.MinecraftSessions.ConnectForLaunchAsync(_lifetime.Token).WaitAsync(_lifetime.Token);
        CheckCurrent();
        try { return await operation(account.MinecraftAccessToken!, _lifetime.Token); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
        {
            CheckCurrent();
            account = await _app.MinecraftSessions.RefreshAsync(_lifetime.Token).WaitAsync(_lifetime.Token);
            CheckCurrent();
            return await operation(account.MinecraftAccessToken!, _lifetime.Token);
        }
    }

    private static string ErrorResource(Exception error, string fallback) => error switch
    {
        AuthServiceException => "CharacterSessionError",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "CharacterSessionError",
        InvalidOperationException { Message: "CharacterSaveUnconfirmed" } => "CharacterSaveUnconfirmed",
        _ => fallback
    };
    private void CheckCurrent()
    {
        if (!Current) throw new OperationCanceledException(_lifetime.Token);
    }
    private void Notify() { if (Current) Changed?.Invoke(this, EventArgs.Empty); }
    private void AccountChanged(object? sender, EventArgs e) => _lifetime.Cancel();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _app.AccountGenerationChanged -= AccountChanged;
        _lifetime.Cancel();
        // In-flight requests may still observe Token; dispose after their cancellation has propagated.
    }
}
