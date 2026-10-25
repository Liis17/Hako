using System.Numerics;
using System.Runtime.InteropServices;
using Hako.Graphics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Hako.Tests;

[TestClass]
public sealed class MinecraftCapeRenderTests
{
    [TestMethod]
    public void Cape_RenderedPixelsDoNotDependOnTriangleSubmissionOrder()
    {
        var opaque = new MinecraftSkinTriangle[72];
        var layers = new MinecraftSkinTriangle[84];
        foreach (var yaw in new[] { 0.4f, 2.8f, 3.6f })
        {
            MinecraftSkinGeometry.BuildFrame(false, true, yaw, 0.2f, 0.375, true,
                opaque, layers, out _, out var count);
            var cape = layers[..count].Where(t => t.A.UV.Y > 64f / 96).ToArray();
            var forward = Render(cape);
            var reverse = Render(cape.AsEnumerable().Reverse().ToArray());
            Assert.IsTrue(forward.Any(b => b != 0), "The cape must cover pixels.");
            CollectionAssert.AreEqual(forward, reverse, $"Opposite cape faces overlap at yaw {yaw}.");
        }
    }

    private static unsafe byte[] Render(MinecraftSkinTriangle[] triangles)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None,
            new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using (device)
        using (context)
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MinecraftSkin.hlsl"));
            var vsCode = Compiler.Compile(source, "VSMain", "test", "vs_5_0").ToArray();
            var psCode = Compiler.Compile(source, "PSTranslucent", "test", "ps_5_0").ToArray();
            using var vs = device.CreateVertexShader(vsCode);
            using var ps = device.CreatePixelShader(psCode);
            using var layout = device.CreateInputLayout([
                new InputElementDescription("POSITION", 0, Format.R32G32B32A32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 16, 0),
                new InputElementDescription("SHADE", 0, Format.R32_Float, 24, 0)], vsCode);
            var vertices = triangles.SelectMany(t => new[] { Vertex(t.A), Vertex(t.B), Vertex(t.C) }).ToArray();
            using var buffer = device.CreateBuffer(vertices, BindFlags.VertexBuffer);
            using var target = device.CreateTexture2D(Format.R8G8B8A8_UNorm, 96, 96, bindFlags: BindFlags.RenderTarget);
            using var view = device.CreateRenderTargetView(target);
            using var depth = device.CreateTexture2D(Format.D32_Float, 96, 96, bindFlags: BindFlags.DepthStencil);
            using var depthView = device.CreateDepthStencilView(depth);
            using var depthState = device.CreateDepthStencilState(MinecraftSkinPipeline.LayerDepth);
            using var rasterizer = device.CreateRasterizerState(RasterizerDescription.CullNone);
            using var sampler = device.CreateSamplerState(SamplerDescription.PointClamp);
            using var atlas = device.CreateTexture2D(Format.R8G8B8A8_UNorm, 64, 96, bindFlags: BindFlags.ShaderResource);
            using var atlasView = device.CreateShaderResourceView(atlas);
            var pixels = new byte[64 * 96 * 4];
            for (var y = 64; y < 96; y++)
            for (var x = 0; x < 64; x++)
            {
                var offset = (y * 64 + x) * 4;
                pixels[offset] = x < 11 ? (byte)255 : (byte)0;
                pixels[offset + 1] = x >= 11 ? (byte)255 : (byte)0;
                pixels[offset + 3] = 255;
            }
            fixed (byte* p = pixels) context.UpdateSubresource(atlas, 0, (Box?)null, (IntPtr)p, 64 * 4, (uint)pixels.Length);
            context.ClearRenderTargetView(view, new Color4(0, 0, 0, 0));
            context.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth, 1, 0);
            context.OMSetRenderTargets(view, depthView);
            context.OMSetDepthStencilState(depthState);
            context.RSSetState(rasterizer);
            context.RSSetViewport(new Viewport(0, 0, 96, 96));
            context.IASetInputLayout(layout);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.IASetVertexBuffer(0, buffer, 28, 0);
            context.VSSetShader(vs);
            context.PSSetShader(ps);
            context.PSSetShaderResource(0, atlasView);
            context.PSSetSampler(0, sampler);
            context.Draw((uint)vertices.Length, 0);
            context.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>());
            var description = target.Description;
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            using var staging = device.CreateTexture2D(description);
            context.CopyResource(staging, target);
            var mapped = context.Map(staging, 0, MapMode.Read);
            try
            {
                var result = new byte[96 * 96 * 4];
                for (var row = 0; row < 96; row++)
                    Marshal.Copy(mapped.DataPointer + row * (int)mapped.RowPitch, result, row * 384, 384);
                return result;
            }
            finally { context.Unmap(staging, 0); }
        }
    }

    private static TestVertex Vertex(MinecraftSkinVertex v) => new(
        new Vector4(v.Position.X, v.Position.Y, 0.5f - v.Position.Z * 0.2f, 1), v.UV, 1);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TestVertex(Vector4 Position, Vector2 UV, float Shade);
}

