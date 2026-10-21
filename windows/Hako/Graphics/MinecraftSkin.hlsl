Texture2D<float4> SkinAtlas : register(t0);
SamplerState PointSampler : register(s0);

struct VertexInput
{
    float4 Position : POSITION;
    float2 UV : TEXCOORD0;
    float Shade : SHADE0;
};

struct PixelInput
{
    float4 Position : SV_POSITION;
    float2 UV : TEXCOORD0;
    float Shade : SHADE0;
};

PixelInput VSMain(VertexInput input)
{
    PixelInput output;
    output.Position = input.Position;
    output.UV = input.UV;
    output.Shade = input.Shade;
    return output;
}

float4 PSOpaque(PixelInput input) : SV_TARGET
{
    float4 color = SkinAtlas.Sample(PointSampler, input.UV);
    return float4(color.rgb * input.Shade, 1.0);
}

float4 PSTranslucent(PixelInput input) : SV_TARGET
{
    float4 color = SkinAtlas.Sample(PointSampler, input.UV);
    clip(color.a - 0.5);
    return float4(color.rgb * input.Shade * color.a, color.a);
}
