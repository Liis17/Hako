using System.Numerics;
using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinRT;

namespace Hako.Graphics;

public enum MinecraftSkinRendererFailure
{
    Initialization,
    CompositionSurface,
    Rendering,
    DeviceLost
}

public sealed class MinecraftSkinRendererException : Exception
{
    public MinecraftSkinRendererException(MinecraftSkinRendererFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public MinecraftSkinRendererFailure Failure { get; }
}

/// <summary>
/// Native D3D11 Minecraft skin renderer attached to a WinUI composition visual.
/// Texture input is top-to-bottom row-major straight RGBA; yaw and pitch are radians.
/// </summary>
public sealed class MinecraftSkinRenderer : IDisposable
{
    public const float InitialYawRadians = MinecraftSkinGeometry.InitialYawRadians;
    public const float InitialPitchRadians = MinecraftSkinGeometry.InitialPitchRadians;

    private const int AtlasWidth = 64;
    private const int AtlasHeight = 96;
    private const int SkinByteLength = 64 * 64 * 4;
    private const int CapeByteLength = 64 * 32 * 4;
    private const int VertexStride = sizeof(float) * 8;
    private const int MaxVertexCount = 468;
    private const int MaxOpaqueTriangleCount = MinecraftSkinGeometry.OpaqueTriangleCapacity;
    private const int MaxTransparentTriangleCount = MinecraftSkinGeometry.TranslucentTriangleCapacity;
    private const float FieldOfViewRadians = 38f * MathF.PI / 180f;
    private const float NearPlane = 0.01f;
    private const float FarPlane = 100f;

    private readonly FrameworkElement _host;
    private readonly byte[] _atlasPixels = new byte[AtlasWidth * AtlasHeight * 4];
    private readonly Vertex[] _frameVertices = new Vertex[MaxVertexCount];
    private readonly MinecraftSkinTriangle[] _opaqueTriangles = new MinecraftSkinTriangle[MaxOpaqueTriangleCount];
    private readonly MinecraftSkinTriangle[] _transparentTriangles = new MinecraftSkinTriangle[MaxTransparentTriangleCount];
    private readonly byte[] _vertexShaderBytecode;
    private readonly byte[] _opaquePixelShaderBytecode;
    private readonly byte[] _translucentPixelShaderBytecode;

    private Compositor? _compositor;
    private SpriteVisual? _spriteVisual;
    private CompositionSurfaceBrush? _surfaceBrush;
    private CompositionGraphicsDevice? _compositionGraphicsDevice;
    private CompositionDrawingSurface? _drawingSurface;
    private ICompositionDrawingSurfaceInterop? _drawingSurfaceInterop;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _opaquePixelShader;
    private ID3D11PixelShader? _translucentPixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Texture2D? _atlasTexture;
    private ID3D11ShaderResourceView? _atlasView;
    private ID3D11SamplerState? _sampler;
    private ID3D11RasterizerState? _rasterizerState;
    private ID3D11DepthStencilState? _opaqueDepthState;
    private ID3D11DepthStencilState? _translucentDepthState;
    private ID3D11BlendState? _translucentBlendState;
    private ID3D11Texture2D? _renderTargetTexture;
    private ID3D11RenderTargetView? _renderTargetView;
    private ID3D11Texture2D? _depthTexture;
    private ID3D11DepthStencilView? _depthView;

    private int _pixelWidth;
    private int _pixelHeight;
    private bool _hasSkin;
    private bool _hasCape;
    private bool _slim;
    private bool _disposed;

    public MinecraftSkinRenderer(FrameworkElement host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));

        try
        {
            _vertexShaderBytecode = CompileShader("VSMain", "vs_5_0");
            _opaquePixelShaderBytecode = CompileShader("PSOpaque", "ps_5_0");
            _translucentPixelShaderBytecode = CompileShader("PSTranslucent", "ps_5_0");

            _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            _surfaceBrush = _compositor.CreateSurfaceBrush();
            _surfaceBrush.Stretch = CompositionStretch.Fill;
            _spriteVisual = _compositor.CreateSpriteVisual();
            _spriteVisual.Brush = _surfaceBrush;
            _spriteVisual.Size = Vector2.Zero;
            ElementCompositionPreview.SetElementChildVisual(host, _spriteVisual);

            InitializeDeviceChain();
        }
        catch (Exception exception)
        {
            Dispose();
            throw new MinecraftSkinRendererException(
                MinecraftSkinRendererFailure.Initialization,
                "Не удалось создать D3D11-рендерер скина Minecraft.",
                exception);
        }
    }

    /// <summary>
    /// Resizes the composition surface in physical pixels while keeping the SpriteVisual in DIPs.
    /// A zero dimension or scale releases the size-dependent surfaces and skips rendering.
    /// </summary>
    public void Resize(double widthDip, double heightDip, double rasterizationScale)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(widthDip) || !double.IsFinite(heightDip) || !double.IsFinite(rasterizationScale)
            || widthDip < 0 || heightDip < 0 || rasterizationScale < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthDip), "Размер и масштаб должны быть конечными и неотрицательными.");
        }

        if (widthDip == 0 || heightDip == 0 || rasterizationScale == 0)
        {
            _pixelWidth = 0;
            _pixelHeight = 0;
            _spriteVisual!.Size = Vector2.Zero;
            ReleaseSizeResources();
            return;
        }

        var widthPixels = checked((int)Math.Round(widthDip * rasterizationScale, MidpointRounding.AwayFromZero));
        var heightPixels = checked((int)Math.Round(heightDip * rasterizationScale, MidpointRounding.AwayFromZero));
        if (widthPixels <= 0 || heightPixels <= 0)
        {
            _pixelWidth = 0;
            _pixelHeight = 0;
            _spriteVisual!.Size = Vector2.Zero;
            ReleaseSizeResources();
            return;
        }

        if (widthPixels > ID3D11Resource.MaximumTexture2DSize || heightPixels > ID3D11Resource.MaximumTexture2DSize)
        {
            throw new ArgumentOutOfRangeException(nameof(widthDip), "Размер поверхности превышает предел D3D11.");
        }

        if (widthPixels == _pixelWidth && heightPixels == _pixelHeight && _drawingSurfaceInterop is not null)
        {
            _spriteVisual!.Size = new Vector2((float)widthDip, (float)heightDip);
            return;
        }

        try
        {
            CreateSizeResources(widthPixels, heightPixels);
            _pixelWidth = widthPixels;
            _pixelHeight = heightPixels;
            _spriteVisual!.Size = new Vector2((float)widthDip, (float)heightDip);
        }
        catch (Exception exception) when (IsDeviceLost(exception))
        {
            _pixelWidth = widthPixels;
            _pixelHeight = heightPixels;
            RecoverDevice();
            _spriteVisual!.Size = new Vector2((float)widthDip, (float)heightDip);
        }
        catch (MinecraftSkinRendererException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MinecraftSkinRendererException(
                MinecraftSkinRendererFailure.CompositionSurface,
                "Не удалось изменить размер поверхности композиции Minecraft.",
                exception);
        }
    }

    /// <summary>
    /// Updates a normalized 64x64 straight-RGBA skin and optional 64x32 straight-RGBA cape.
    /// Pixel data must be row-major from the top-left; arrays are copied before this method returns.
    /// </summary>
    public void UpdateSkin(byte[] skinRgba64x64, bool slim, byte[]? capeRgba64x32 = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(skinRgba64x64);
        if (skinRgba64x64.Length != SkinByteLength)
        {
            throw new ArgumentException("Текстура скина должна иметь размер 64×64 RGBA.", nameof(skinRgba64x64));
        }
        if (capeRgba64x32 is not null && capeRgba64x32.Length != CapeByteLength)
        {
            throw new ArgumentException("Текстура плаща должна иметь размер 64×32 RGBA.", nameof(capeRgba64x32));
        }

        Array.Clear(_atlasPixels);
        Buffer.BlockCopy(skinRgba64x64, 0, _atlasPixels, 0, SkinByteLength);
        if (capeRgba64x32 is not null)
        {
            Buffer.BlockCopy(capeRgba64x32, 0, _atlasPixels, SkinByteLength, CapeByteLength);
        }

        _slim = slim;
        _hasCape = capeRgba64x32 is not null;
        _hasSkin = true;

        try
        {
            UploadAtlas();
        }
        catch (Exception exception) when (IsDeviceLost(exception))
        {
            RecoverDevice();
        }
        catch (Exception exception)
        {
            throw new MinecraftSkinRendererException(
                MinecraftSkinRendererFailure.Rendering,
                "Не удалось передать текстуру скина в D3D11.",
                exception);
        }
    }

    /// <summary>
    /// Renders a frame. +Y is up; yaw rotates around +Y and pitch around +X, both in radians.
    /// Animation uses a 1.5-second cycle when walking; when stopped it renders the resting pose.
    /// </summary>
    public void Render(
        float yawRadians = InitialYawRadians,
        float pitchRadians = InitialPitchRadians,
        TimeSpan animationElapsed = default,
        bool isWalking = false)
    {
        ThrowIfDisposed();
        if (_pixelWidth <= 0 || _pixelHeight <= 0 || !_hasSkin || _drawingSurfaceInterop is null)
        {
            return;
        }
        if (!float.IsFinite(yawRadians) || !float.IsFinite(pitchRadians) || !double.IsFinite(animationElapsed.TotalSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(yawRadians), "Поза должна содержать конечные значения.");
        }

        try
        {
            DrawFrame(yawRadians, pitchRadians, animationElapsed, isWalking);
            _device!.DeviceRemovedReason.CheckError();
        }
        catch (Exception exception) when (IsDeviceLost(exception))
        {
            RecoverDevice();
        }
        catch (MinecraftSkinRendererException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MinecraftSkinRendererException(
                MinecraftSkinRendererFailure.Rendering,
                "Не удалось отрисовать Minecraft-скин.",
                exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            ElementCompositionPreview.SetElementChildVisual(_host, null!);
        }
        catch
        {
            // The host may already be closing as its owner disposes this renderer.
        }

        ReleaseSizeResources();
        CloseCompositionObject(_surfaceBrush);
        _surfaceBrush = null;
        CloseCompositionObject(_spriteVisual);
        _spriteVisual = null;
        CloseCompositionObject(_compositionGraphicsDevice);
        _compositionGraphicsDevice = null;
        ReleaseDeviceResources();
        _compositor = null;
        GC.SuppressFinalize(this);
    }

    private void DrawFrame(float yawRadians, float pitchRadians, TimeSpan animationElapsed, bool isWalking)
    {
        BuildFrame(yawRadians, pitchRadians, animationElapsed, isWalking, out var opaqueVertexCount, out var totalVertexCount);

        var mapped = _context!.Map(_vertexBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            _frameVertices.AsSpan(0, totalVertexCount).CopyTo(mapped.AsSpan<Vertex>(totalVertexCount));
        }
        finally
        {
            _context.Unmap(_vertexBuffer!, 0);
        }

        var clear = new Color4(0, 0, 0, 0);
        _context.ClearRenderTargetView(_renderTargetView!, clear);
        _context.ClearDepthStencilView(_depthView!, DepthStencilClearFlags.Depth, 1f, 0);
        _context.OMSetRenderTargets(_renderTargetView!, _depthView!);
        _context.RSSetViewport(new Viewport(0, 0, _pixelWidth, _pixelHeight, 0, 1));
        _context.RSSetState(_rasterizerState);
        _context.IASetInputLayout(_inputLayout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetVertexBuffer(0, _vertexBuffer!, VertexStride, 0);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShaderResource(0, _atlasView!);
        _context.PSSetSampler(0, _sampler);

        _context.OMSetBlendState(null);
        _context.OMSetDepthStencilState(_opaqueDepthState, 0);
        _context.PSSetShader(_opaquePixelShader);
        _context.Draw((uint)opaqueVertexCount, 0);

        if (totalVertexCount > opaqueVertexCount)
        {
            _context.OMSetBlendState(_translucentBlendState);
            _context.OMSetDepthStencilState(_translucentDepthState, 0);
            _context.PSSetShader(_translucentPixelShader);
            _context.Draw((uint)(totalVertexCount - opaqueVertexCount), (uint)opaqueVertexCount);
        }

        _context.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>());
        using var surfaceSession = _drawingSurfaceInterop!.BeginDraw();
        var destinationDescription = surfaceSession.Texture.Description;
        var destinationX = checked((uint)surfaceSession.Offset.X);
        var destinationY = checked((uint)surfaceSession.Offset.Y);
        if (destinationX + (uint)_pixelWidth > destinationDescription.Width
            || destinationY + (uint)_pixelHeight > destinationDescription.Height)
        {
            throw new InvalidOperationException("BeginDraw вернул поверхность меньше области копирования.");
        }

        var sourceBox = new Box(0, 0, 0, _pixelWidth, _pixelHeight, 1);
        _context.CopySubresourceRegion(
            surfaceSession.Texture,
            0,
            destinationX,
            destinationY,
            0,
            _renderTargetTexture!,
            0,
            sourceBox);
    }

    private void BuildFrame(
        float yawRadians,
        float pitchRadians,
        TimeSpan animationElapsed,
        bool isWalking,
        out int opaqueVertexCount,
        out int totalVertexCount)
    {
        MinecraftSkinGeometry.BuildFrame(
            _slim,
            _hasCape,
            yawRadians,
            pitchRadians,
            animationElapsed.TotalSeconds,
            isWalking,
            _opaqueTriangles,
            _transparentTriangles,
            out var opaqueTriangleCount,
            out var transparentTriangleCount);

        var aspect = (float)_pixelWidth / _pixelHeight;
        var verticalHalfFov = FieldOfViewRadians / 2f;
        var horizontalHalfFov = MathF.Atan(MathF.Tan(verticalHalfFov) * aspect);
        var cameraDistance = 1.25f / MathF.Sin(MathF.Min(verticalHalfFov, horizontalHalfFov));
        var focalLength = 1f / MathF.Tan(verticalHalfFov);
        var destination = 0;

        for (var index = 0; index < opaqueTriangleCount; index++)
        {
            AppendProjectedTriangle(_opaqueTriangles[index], cameraDistance, focalLength, aspect, ref destination);
        }

        opaqueVertexCount = destination;
        for (var index = 0; index < transparentTriangleCount; index++)
        {
            AppendProjectedTriangle(_transparentTriangles[index], cameraDistance, focalLength, aspect, ref destination);
        }
        totalVertexCount = destination;
    }

    private void AppendProjectedTriangle(
        MinecraftSkinTriangle triangle,
        float cameraDistance,
        float focalLength,
        float aspect,
        ref int destination)
    {
        _frameVertices[destination++] = ProjectVertex(triangle.A, cameraDistance, focalLength, aspect);
        _frameVertices[destination++] = ProjectVertex(triangle.B, cameraDistance, focalLength, aspect);
        _frameVertices[destination++] = ProjectVertex(triangle.C, cameraDistance, focalLength, aspect);
    }

    private static Vertex ProjectVertex(MinecraftSkinVertex vertex, float cameraDistance, float focalLength, float aspect)
    {
        var viewZ = vertex.Position.Z - cameraDistance;
        var clipW = -viewZ;
        var clipX = vertex.Position.X * focalLength / aspect;
        var clipY = vertex.Position.Y * focalLength;
        var clipZ = viewZ * (FarPlane / (NearPlane - FarPlane))
            + (NearPlane * FarPlane / (NearPlane - FarPlane));
        return new Vertex(new Vector4(clipX, clipY, clipZ, clipW), vertex.UV, vertex.Shade);
    }
    private void InitializeDeviceChain()
    {
        Exception? hardwareFailure = null;
        try
        {
            InitializeDeviceResources(DriverType.Hardware);
            CreateCompositionGraphicsDevice();
            return;
        }
        catch (Exception exception)
        {
            hardwareFailure = exception;
            ReleaseCompositionGraphicsDevice();
            ReleaseDeviceResources();
        }

        try
        {
            InitializeDeviceResources(DriverType.Warp);
            CreateCompositionGraphicsDevice();
        }
        catch (Exception warpFailure)
        {
            ReleaseCompositionGraphicsDevice();
            ReleaseDeviceResources();
            throw new MinecraftSkinRendererException(
                MinecraftSkinRendererFailure.Initialization,
                "Не удалось создать D3D11-устройство ни на GPU, ни на WARP.",
                new AggregateException(hardwareFailure!, warpFailure));
        }
    }

    private void InitializeDeviceResources(DriverType driverType)
    {
        ID3D11Device device;
        ID3D11DeviceContext context;
        D3D11.D3D11CreateDevice(
            null,
            driverType,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out device,
            out context).CheckError();

        _device = device;
        _context = context;
        CreatePipelineResources();
        CreateAtlasResources();
    }

    private void CreateCompositionGraphicsDevice()
    {
        _compositionGraphicsDevice = ICompositorInterop.CreateGraphicsDevice(_compositor!, _device!);
    }

    private void CreatePipelineResources()
    {
        _vertexShader = _device!.CreateVertexShader(_vertexShaderBytecode);
        _opaquePixelShader = _device.CreatePixelShader(_opaquePixelShaderBytecode);
        _translucentPixelShader = _device.CreatePixelShader(_translucentPixelShaderBytecode);
        _inputLayout = _device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32A32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, sizeof(float) * 4, 0),
            new InputElementDescription("SHADE", 0, Format.R32_Float, sizeof(float) * 6, 0)
        ], _vertexShaderBytecode);

        _vertexBuffer = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = MaxVertexCount * VertexStride,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.VertexBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });
        _rasterizerState = _device.CreateRasterizerState(RasterizerDescription.CullNone);
        _opaqueDepthState = _device.CreateDepthStencilState(DepthStencilDescription.Default);
        _translucentDepthState = _device.CreateDepthStencilState(DepthStencilDescription.DepthRead);
        _translucentBlendState = _device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha));
        _sampler = _device.CreateSamplerState(SamplerDescription.PointClamp);
    }

    private void CreateAtlasResources()
    {
        _atlasTexture = _device!.CreateTexture2D(
            Format.R8G8B8A8_UNorm,
            AtlasWidth,
            AtlasHeight,
            mipLevels: 1,
            bindFlags: BindFlags.ShaderResource);
        _atlasView = _device.CreateShaderResourceView(_atlasTexture);
        UploadAtlas();
    }

    private unsafe void UploadAtlas()
    {
        if (_atlasTexture is null || _context is null)
        {
            return;
        }

        fixed (byte* pixelPointer = _atlasPixels)
        {
            _context.UpdateSubresource(
                _atlasTexture,
                0,
                null,
                (IntPtr)pixelPointer,
                AtlasWidth * 4,
                AtlasWidth * AtlasHeight * 4);
        }
    }

    private void CreateSizeResources(int width, int height)
    {
        ID3D11Texture2D? renderTexture = null;
        ID3D11RenderTargetView? renderView = null;
        ID3D11Texture2D? depthTexture = null;
        ID3D11DepthStencilView? depthView = null;
        CompositionDrawingSurface? drawingSurface = null;
        ICompositionDrawingSurfaceInterop? drawingSurfaceInterop = null;

        try
        {
            renderTexture = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            });
            renderView = _device.CreateRenderTargetView(renderTexture);
            depthTexture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.D24_UNorm_S8_UInt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            });
            depthView = _device.CreateDepthStencilView(depthTexture);

            drawingSurface = _compositionGraphicsDevice!.CreateDrawingSurface(
                new Windows.Foundation.Size(width, height),
                Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
            drawingSurfaceInterop = ICompositionDrawingSurfaceInterop.Create(drawingSurface);

            ReleaseSizeResources();
            _surfaceBrush!.Surface = drawingSurface;
            _renderTargetTexture = renderTexture;
            _renderTargetView = renderView;
            _depthTexture = depthTexture;
            _depthView = depthView;
            _drawingSurface = drawingSurface;
            _drawingSurfaceInterop = drawingSurfaceInterop;
            renderTexture = null;
            renderView = null;
            depthTexture = null;
            depthView = null;
            drawingSurface = null;
            drawingSurfaceInterop = null;
        }
        finally
        {
            renderView?.Dispose();
            renderTexture?.Dispose();
            depthView?.Dispose();
            depthTexture?.Dispose();
            drawingSurfaceInterop?.Dispose();
            CloseCompositionObject(drawingSurface);
        }
    }

    private void ReleaseSizeResources()
    {
        _drawingSurfaceInterop?.Dispose();
        _drawingSurfaceInterop = null;
        if (_surfaceBrush is not null)
        {
            _surfaceBrush.Surface = null!;
        }
        CloseCompositionObject(_drawingSurface);
        _drawingSurface = null;
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        _renderTargetTexture?.Dispose();
        _renderTargetTexture = null;
        _depthView?.Dispose();
        _depthView = null;
        _depthTexture?.Dispose();
        _depthTexture = null;
    }

    private void ReleaseCompositionGraphicsDevice()
    {
        ReleaseSizeResources();
        CloseCompositionObject(_compositionGraphicsDevice);
        _compositionGraphicsDevice = null;
    }

    private void ReleaseDeviceResources()
    {
        if (_context is not null)
        {
            _context.ClearState();
            _context.Flush();
        }

        _atlasView?.Dispose();
        _atlasView = null;
        _atlasTexture?.Dispose();
        _atlasTexture = null;
        _sampler?.Dispose();
        _sampler = null;
        _translucentBlendState?.Dispose();
        _translucentBlendState = null;
        _translucentDepthState?.Dispose();
        _translucentDepthState = null;
        _opaqueDepthState?.Dispose();
        _opaqueDepthState = null;
        _rasterizerState?.Dispose();
        _rasterizerState = null;
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        _inputLayout?.Dispose();
        _inputLayout = null;
        _translucentPixelShader?.Dispose();
        _translucentPixelShader = null;
        _opaquePixelShader?.Dispose();
        _opaquePixelShader = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    private void RecoverDevice()
    {
        try
        {
            ReleaseCompositionGraphicsDevice();
            ReleaseDeviceResources();
            InitializeDeviceChain();
            if (_pixelWidth > 0 && _pixelHeight > 0)
            {
                CreateSizeResources(_pixelWidth, _pixelHeight);
            }
        }
        catch (Exception exception)
        {
            throw new MinecraftSkinRendererException(
                MinecraftSkinRendererFailure.DeviceLost,
                "Не удалось пересоздать D3D11 и Composition ресурсы после потери устройства.",
                exception);
        }
    }

    private static byte[] CompileShader(string entryPoint, string profile)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Hako.Graphics.MinecraftSkin.hlsl")
            ?? throw new InvalidOperationException("Ресурс HLSL не найден в сборке Hako.");
        using var reader = new StreamReader(stream);
        return Compiler.Compile(reader.ReadToEnd(), entryPoint, "MinecraftSkin.hlsl", profile,
            shaderFlags: ShaderFlags.OptimizationLevel3).ToArray();
    }

    private static void CloseCompositionObject(IDisposable? compositionObject)
    {
        compositionObject?.Dispose();
    }

    private static bool IsDeviceLost(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var code = unchecked((uint)current.HResult);
            if (code is 0x887A0005 or 0x887A0006 or 0x887A0007 or 0x887A0020)
            {
                return true;
            }
        }
        return false;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct Vertex
    {
        internal Vertex(Vector4 position, Vector2 uv, float shade)
        {
            Position = position;
            UV = uv;
            Shade = shade;
            Padding = 0;
        }

        internal readonly Vector4 Position;
        internal readonly Vector2 UV;
        internal readonly float Shade;
        internal readonly float Padding;
    }

}
