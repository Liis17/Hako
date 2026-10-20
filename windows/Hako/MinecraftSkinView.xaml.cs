using Hako.Core;
using Hako.Graphics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Windows.Foundation.Metadata;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Hako;

public sealed partial class MinecraftSkinView : UserControl
{
    private readonly UISettings _uiSettings = new();
    private MinecraftSkinPreviewService? _previewService;
    private MinecraftSkinRenderer? _renderer;
    private MinecraftSkinPreviewData? _skinData;
    private CancellationTokenSource? _loadCancellation;
    private string? _profileId;
    private Guid _requestScope;
    private bool _fixture;
    private bool _sourceConfigured;
    private bool _requestedEligible;
    private bool _isLoaded;
    private bool _animationsEnabled = true;
    private bool _animationsChangedSubscribed;
    private bool _languageChangedSubscribed;
    private bool _renderingSubscribed;
    private bool _disposed;
    private bool _isDragging;
    private uint _pointerId;
    private Windows.Foundation.Point _dragOrigin;
    private float _dragYaw;
    private float _dragPitch;
    private float _yaw = MinecraftSkinRenderer.InitialYawRadians;
    private float _pitch = MinecraftSkinRenderer.InitialPitchRadians;
    private double _animationElapsedSeconds;
    private long _lastFrameTimestamp;
    private int _loadRevision;
    private XamlRoot? _observedXamlRoot;

    public MinecraftSkinView()
    {
        InitializeComponent();
        UpdateLocalizedProperties();
    }

    public event Action<object?, byte[]>? HeadImageReady;

    internal void SetProfile(
        MinecraftSkinPreviewService previewService,
        string? profileId,
        Guid requestScope,
        bool useFixture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var identityChanged = !_sourceConfigured || !string.Equals(_profileId, profileId, StringComparison.Ordinal);
        var profileChanged = identityChanged || _fixture != useFixture;
        var requestChanged = _requestScope != requestScope;
        _previewService = previewService;
        _sourceConfigured = true;
        _profileId = profileId;
        _fixture = useFixture;
        _requestScope = requestScope;

        if (profileChanged)
        {
            if (identityChanged)
            {
                _yaw = MinecraftSkinRenderer.InitialYawRadians;
                _pitch = MinecraftSkinRenderer.InitialPitchRadians;
                StopRenderingFrames();
                DisposeRenderer();
            }

            _skinData = null;
        }

        if (IsEligible && (profileChanged || requestChanged))
        {
            StartSourceLoad();
        }
    }

    public void SetEligible(bool eligible)
    {
        if (_disposed || _requestedEligible == eligible)
        {
            return;
        }

        _requestedEligible = eligible;
        if (!IsEligible)
        {
            StopActivity(disposeRenderer: true);
            StopObservingXamlRoot();
            UnsubscribeWindowSettings();
            UnsubscribeLanguageChanges();
            return;
        }

        ObserveXamlRoot();
        SubscribeLanguageChanges();
        RefreshAnimationPreference();
        if (_skinData is not null)
        {
            DisplaySkin(_skinData);
        }
        StartSourceLoad();
    }

    public void DisposeView()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _requestedEligible = false;
        StopActivity(disposeRenderer: true);
        StopObservingXamlRoot();
        UnsubscribeWindowSettings();
        UnsubscribeLanguageChanges();
    }

    private bool IsEligible => _requestedEligible && _isLoaded && _sourceConfigured && _previewService is not null;

    private bool IsWalking => IsEligible && _skinData is not null && _animationsEnabled;

    private void MinecraftSkinView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _isLoaded = true;
        ObserveXamlRoot();
        UpdateLocalizedProperties();
        if (IsEligible)
        {
            SubscribeLanguageChanges();
            RefreshAnimationPreference();
            if (_skinData is not null)
            {
                DisplaySkin(_skinData);
            }
            StartSourceLoad();
        }
    }

    private void MinecraftSkinView_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        StopActivity(disposeRenderer: true);
        StopObservingXamlRoot();
        UnsubscribeWindowSettings();
        UnsubscribeLanguageChanges();
    }

    private void HakoStrings_LanguageChanged(object? sender, EventArgs e) => UpdateLocalizedProperties();

    private void UpdateLocalizedProperties()
    {
        AutomationProperties.SetName(this, HakoStrings.Get("SkinViewAccessibilityLabel"));
        AutomationProperties.SetHelpText(this, HakoStrings.Get("SkinViewAccessibilityHint"));
        UnavailableText.Text = HakoStrings.Get("SkinViewUnavailable");
    }

    private void RefreshAnimationPreference()
    {
        if (!_isLoaded || !IsEligible)
        {
            return;
        }

        try
        {
            _animationsEnabled = _uiSettings.AnimationsEnabled;
            if (!_animationsChangedSubscribed
                && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                && ApiInformation.IsEventPresent(
                    "Windows.UI.ViewManagement.UISettings",
                    "AnimationsEnabledChanged"))
            {
                _uiSettings.AnimationsEnabledChanged += UISettings_AnimationsEnabledChanged;
                _animationsChangedSubscribed = true;
            }
        }
        catch (Exception)
        {
            _animationsEnabled = true;
        }

        UpdateFrameSubscription();
        if (!IsWalking)
        {
            RenderPose(isWalking: false);
        }
    }

    private void UISettings_AnimationsEnabledChanged(UISettings sender, object args)
    {
        if (!_isLoaded)
        {
            return;
        }

        _animationsEnabled = sender.AnimationsEnabled;
        UpdateFrameSubscription();
        if (!IsWalking)
        {
            RenderPose(isWalking: false);
        }
    }

    private void UnsubscribeWindowSettings()
    {
        if (_animationsChangedSubscribed)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                _uiSettings.AnimationsEnabledChanged -= UISettings_AnimationsEnabledChanged;
            }

            _animationsChangedSubscribed = false;
        }
    }

    private void SubscribeLanguageChanges()
    {
        if (_isLoaded && IsEligible && !_languageChangedSubscribed)
        {
            HakoStrings.LanguageChanged += HakoStrings_LanguageChanged;
            _languageChangedSubscribed = true;
        }
    }

    private void UnsubscribeLanguageChanges()
    {
        if (_languageChangedSubscribed)
        {
            HakoStrings.LanguageChanged -= HakoStrings_LanguageChanged;
            _languageChangedSubscribed = false;
        }
    }

    private void ObserveXamlRoot()
    {
        if (_observedXamlRoot == XamlRoot)
        {
            return;
        }

        StopObservingXamlRoot();
        _observedXamlRoot = XamlRoot;
        if (_observedXamlRoot is not null)
        {
            _observedXamlRoot.Changed += XamlRoot_Changed;
        }
    }

    private void StopObservingXamlRoot()
    {
        if (_observedXamlRoot is null)
        {
            return;
        }

        _observedXamlRoot.Changed -= XamlRoot_Changed;
        _observedXamlRoot = null;
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (!IsEligible || _renderer is null)
        {
            return;
        }

        try
        {
            _renderer.Resize(RendererSurface.ActualWidth, RendererSurface.ActualHeight, sender.RasterizationScale);
            RenderPose(IsWalking);
        }
        catch (MinecraftSkinRendererException exception)
        {
            HandleRendererFailure(exception);
        }
        catch (Exception)
        {
            HandleRendererFailure(null);
        }
    }

    private void StartSourceLoad()
    {
        if (!IsEligible || _previewService is null)
        {
            return;
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var revision = ++_loadRevision;
        _ = LoadSourceAsync(revision, cancellation.Token);
    }

    private async Task LoadSourceAsync(int revision, CancellationToken cancellationToken)
    {
        var previewService = _previewService!;
        try
        {
            if (_skinData is null)
            {
                var fallback = _fixture
                    ? await previewService.LoadFixtureAsync(cancellationToken)
                    : await previewService.LoadAsync(null, _requestScope, cancellationToken);
                if (!IsCurrentLoad(revision, cancellationToken))
                {
                    return;
                }

                DisplaySkin(fallback);
                if (_fixture || string.IsNullOrWhiteSpace(_profileId))
                {
                    return;
                }
            }

            if (_fixture)
            {
                var fixture = await previewService.LoadFixtureAsync(cancellationToken);
                if (IsCurrentLoad(revision, cancellationToken))
                {
                    DisplaySkin(fixture);
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(_profileId))
            {
                return;
            }

            var skin = await previewService.LoadAsync(_profileId, _requestScope, cancellationToken);
            if (IsCurrentLoad(revision, cancellationToken))
            {
                DisplaySkin(skin);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // The bundled Steve remains visible if network or texture decoding fails.
        }
    }

    private bool IsCurrentLoad(int revision, CancellationToken cancellationToken) =>
        IsEligible && revision == _loadRevision && !cancellationToken.IsCancellationRequested;

    private void StopActivity(bool disposeRenderer)
    {
        StopRenderingFrames();
        _loadRevision++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        if (disposeRenderer)
        {
            DisposeRenderer();
        }
    }

    private void DisplaySkin(MinecraftSkinPreviewData skin)
    {
        _skinData = skin;
        HeadImageReady?.Invoke(this, skin.HeadRgba);
        if (!IsEligible)
        {
            return;
        }

        try
        {
            EnsureRenderer();
            _renderer!.UpdateSkin(skin.SkinRgba, skin.Variant == MinecraftSkinVariant.Slim, skin.CapeRgba);
            UnavailablePanel.Visibility = Visibility.Collapsed;
            RenderPose(IsWalking);
            UpdateFrameSubscription();
        }
        catch (MinecraftSkinRendererException exception)
        {
            HandleRendererFailure(exception);
        }
        catch (Exception)
        {
            HandleRendererFailure(null);
        }
    }

    private void EnsureRenderer()
    {
        if (_renderer is null)
        {
            _renderer = new MinecraftSkinRenderer(RendererSurface);
        }

        var scale = XamlRoot?.RasterizationScale ?? 1;
        _renderer.Resize(RendererSurface.ActualWidth, RendererSurface.ActualHeight, scale);
    }

    private void RenderPose(bool isWalking)
    {
        if (!IsEligible || _renderer is null || _skinData is null)
        {
            return;
        }

        try
        {
            _renderer.Render(_yaw, _pitch, TimeSpan.FromSeconds(_animationElapsedSeconds), isWalking);
        }
        catch (MinecraftSkinRendererException exception)
        {
            HandleRendererFailure(exception);
        }
        catch (Exception)
        {
            HandleRendererFailure(null);
        }
    }

    private void HandleRendererFailure(MinecraftSkinRendererException? exception)
    {
        StopRenderingFrames();
        DisposeRenderer();
        if (exception?.Failure == MinecraftSkinRendererFailure.DeviceLost && _skinData is not null && IsEligible)
        {
            try
            {
                EnsureRenderer();
                _renderer!.UpdateSkin(_skinData.SkinRgba, _skinData.Variant == MinecraftSkinVariant.Slim, _skinData.CapeRgba);
                _renderer.Render(_yaw, _pitch, TimeSpan.FromSeconds(_animationElapsedSeconds), isWalking: false);
                UnavailablePanel.Visibility = Visibility.Collapsed;
                UpdateFrameSubscription();
                return;
            }
            catch (Exception)
            {
                DisposeRenderer();
            }
        }

        UnavailablePanel.Visibility = Visibility.Visible;
    }

    private void RendererSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsEligible || _renderer is null)
        {
            return;
        }

        try
        {
            _renderer.Resize(e.NewSize.Width, e.NewSize.Height, XamlRoot?.RasterizationScale ?? 1);
            RenderPose(IsWalking);
        }
        catch (MinecraftSkinRendererException exception)
        {
            HandleRendererFailure(exception);
        }
        catch (Exception)
        {
            HandleRendererFailure(null);
        }
    }

    private void UpdateFrameSubscription()
    {
        if (IsWalking)
        {
            if (_renderingSubscribed)
            {
                return;
            }

            _lastFrameTimestamp = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += CompositionTarget_Rendering;
            _renderingSubscribed = true;
        }
        else
        {
            StopRenderingFrames();
        }
    }

    private void CompositionTarget_Rendering(object? sender, object args)
    {
        if (!IsWalking || _renderer is null)
        {
            StopRenderingFrames();
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        var deltaSeconds = (timestamp - _lastFrameTimestamp) / (double)Stopwatch.Frequency;
        _lastFrameTimestamp = timestamp;
        _animationElapsedSeconds += Math.Clamp(deltaSeconds, 0, 0.1);
        RenderPose(isWalking: true);
    }

    private void StopRenderingFrames()
    {
        if (_renderingSubscribed)
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _renderingSubscribed = false;
        }
    }

    private void DisposeRenderer()
    {
        if (_renderer is null)
        {
            return;
        }

        _renderer.Dispose();
        _renderer = null;
    }

    private void RendererSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_requestedEligible || !e.GetCurrentPoint(RendererSurface).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        _pointerId = e.Pointer.PointerId;
        _dragOrigin = e.GetCurrentPoint(RendererSurface).Position;
        _dragYaw = _yaw;
        _dragPitch = _pitch;
        RendererSurface.CapturePointer(e.Pointer);
        RendererSurface.Focus(FocusState.Pointer);
        e.Handled = true;
    }

    private void RendererSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || e.Pointer.PointerId != _pointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(RendererSurface);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndDrag(e.Pointer);
            return;
        }

        var width = Math.Max(RendererSurface.ActualWidth, 1);
        var yawDelta = (float)((point.Position.X - _dragOrigin.X) / width * (2 * Math.PI));
        var pitchDelta = (float)((point.Position.Y - _dragOrigin.Y) * 0.3 * Math.PI / 180);
        _yaw = _dragYaw + yawDelta;
        _pitch = Math.Clamp(_dragPitch + pitchDelta, -MathF.PI / 4, MathF.PI / 4);
        RenderPose(IsWalking);
        e.Handled = true;
    }

    private void RendererSurface_PointerReleased(object sender, PointerRoutedEventArgs e) => EndDrag(e.Pointer);

    private void RendererSurface_PointerCanceled(object sender, PointerRoutedEventArgs e) => EndDrag(e.Pointer);

    private void EndDrag(Pointer pointer)
    {
        if (!_isDragging || pointer.PointerId != _pointerId)
        {
            return;
        }

        _isDragging = false;
        RendererSurface.ReleasePointerCapture(pointer);
    }

    private void MinecraftSkinView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        const float step = 15 * MathF.PI / 180;
        switch (e.Key)
        {
            case VirtualKey.Left:
                _yaw -= step;
                break;
            case VirtualKey.Right:
                _yaw += step;
                break;
            case VirtualKey.Up:
                _pitch = Math.Clamp(_pitch + step, -MathF.PI / 4, MathF.PI / 4);
                break;
            case VirtualKey.Down:
                _pitch = Math.Clamp(_pitch - step, -MathF.PI / 4, MathF.PI / 4);
                break;
            default:
                return;
        }

        RenderPose(IsWalking);
        e.Handled = true;
    }
}
