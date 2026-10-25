using Vortice.Direct3D11;

namespace Hako.Graphics;

internal static class MinecraftSkinPipeline
{
    // The shader discards transparent texels. Surviving cape/layer fragments must write depth:
    // sorting triangle centres cannot order the opposite faces of a thin, tilted cape.
    public static DepthStencilDescription LayerDepth => DepthStencilDescription.Default;
}
