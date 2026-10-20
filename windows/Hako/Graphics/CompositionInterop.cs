using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Vortice.Direct3D11;
using WinRT;
using WinRT.Interop;

namespace Hako.Graphics;

internal static unsafe class ICompositorInterop
{
    private static readonly Guid InterfaceId = new("FAB19398-6D19-4D8A-B752-8F096C396069");

    internal static CompositionGraphicsDevice CreateGraphicsDevice(Compositor compositor, ID3D11Device device)
    {
        using var interop = ((IWinRTObject)compositor).NativeObject.As<UnknownVftbl>(InterfaceId);
        var vtable = *(IntPtr**)interop.ThisPtr;
        var createGraphicsDevice = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)vtable[3];
        IntPtr graphicsDevicePointer = IntPtr.Zero;
        var result = createGraphicsDevice(interop.ThisPtr, device.NativePointer, &graphicsDevicePointer);

        try
        {
            Marshal.ThrowExceptionForHR(result);
            return MarshalInspectable<CompositionGraphicsDevice>.FromAbi(graphicsDevicePointer);
        }
        finally
        {
            MarshalInspectable<CompositionGraphicsDevice>.DisposeAbi(graphicsDevicePointer);
        }
    }
}

internal sealed unsafe class ICompositionDrawingSurfaceInterop : IDisposable
{
    private static readonly Guid InterfaceId = new("2D6355C2-AD57-4EAE-92E4-4C3EFF65D578");
    private static readonly Guid Texture2DInterfaceId = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private readonly ObjectReference<UnknownVftbl> _interop;
    private bool _drawInProgress;

    private ICompositionDrawingSurfaceInterop(ObjectReference<UnknownVftbl> interop)
    {
        _interop = interop;
    }

    internal static ICompositionDrawingSurfaceInterop Create(CompositionDrawingSurface surface)
    {
        var interop = ((IWinRTObject)surface).NativeObject.As<UnknownVftbl>(InterfaceId);
        return new ICompositionDrawingSurfaceInterop(interop);
    }

    internal DrawingSurfaceSession BeginDraw()
    {
        if (_drawInProgress)
        {
            throw new InvalidOperationException("A composition drawing surface is already being drawn.");
        }

        var vtable = *(IntPtr**)_interop.ThisPtr;
        var beginDraw = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, NativePoint*, int>)vtable[3];
        var textureInterfaceId = Texture2DInterfaceId;
        IntPtr texturePointer = IntPtr.Zero;
        NativePoint offset = default;
        var result = beginDraw(_interop.ThisPtr, IntPtr.Zero, &textureInterfaceId, &texturePointer, &offset);
        if (result < 0)
        {
            if (texturePointer != IntPtr.Zero)
            {
                Marshal.Release(texturePointer);
            }
            Marshal.ThrowExceptionForHR(result);
        }
        _drawInProgress = true;

        try
        {
            var texture = SharpGen.Runtime.MarshallingHelpers.FromPointer<ID3D11Texture2D>(texturePointer);
            if (texture is null)
            {
                throw new InvalidOperationException("BeginDraw не вернул текстуру D3D11.");
            }
            texturePointer = IntPtr.Zero;
            return new DrawingSurfaceSession(this, texture, offset);
        }
        catch
        {
            if (texturePointer != IntPtr.Zero)
            {
                Marshal.Release(texturePointer);
            }

            EndDraw();
            throw;
        }
    }

    private void EndDraw()
    {
        if (!_drawInProgress)
        {
            return;
        }

        _drawInProgress = false;
        var vtable = *(IntPtr**)_interop.ThisPtr;
        var endDraw = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtable[4];
        Marshal.ThrowExceptionForHR(endDraw(_interop.ThisPtr));
    }

    public void Dispose()
    {
        _interop.Dispose();
    }

    internal sealed class DrawingSurfaceSession : IDisposable
    {
        private readonly ICompositionDrawingSurfaceInterop _owner;

        internal DrawingSurfaceSession(ICompositionDrawingSurfaceInterop owner, ID3D11Texture2D texture, NativePoint offset)
        {
            _owner = owner;
            Texture = texture;
            Offset = offset;
        }

        internal ID3D11Texture2D Texture { get; }
        internal NativePoint Offset { get; }

        public void Dispose()
        {
            try
            {
                Texture.Dispose();
            }
            finally
            {
                _owner.EndDraw();
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    internal int X;
    internal int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UnknownVftbl
{
    internal IntPtr QueryInterface;
    internal IntPtr AddRef;
    internal IntPtr Release;
}
