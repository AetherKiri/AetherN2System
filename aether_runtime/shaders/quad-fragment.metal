#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct AlphaMaskPos
{
    float2 _AlphaMaskPos;
    float2 _padding;
};

struct main0_out
{
    float4 OutColor [[color(0)]];
};

struct main0_in
{
    float4 fs_Color [[user(locn0)]];
    float2 fs_TexCoord [[user(locn1)]];
};

fragment main0_out main0(main0_in in [[stage_in]], constant AlphaMaskPos& _33 [[buffer(1)]], texture2d<float> Texture [[texture(0)]], texture2d<float> AlphaMask [[texture(1)]], sampler Sampler [[sampler(0)]], float4 gl_FragCoord [[position]])
{
    main0_out out = {};
    float4 texel = Texture.sample(Sampler, in.fs_TexCoord);
    float2 maskUV = (gl_FragCoord.xy - _33._AlphaMaskPos) / float2(int2(AlphaMask.get_width(), AlphaMask.get_height()));
    float mask = AlphaMask.sample(Sampler, maskUV).x;
    float alpha = (texel.w * in.fs_Color.w) * mask;
    out.OutColor = float4(((texel * in.fs_Color) * alpha).xyz, alpha);
    return out;
}
