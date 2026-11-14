#pragma clang diagnostic ignored "-Wmissing-prototypes"

#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct EnableAlpha
{
    uint _EnableAlpha;
    float3 _padding;
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

static inline __attribute__((always_inline))
float4 sampleTex(thread const float2& uv, texture2d<float> Luma, sampler Sampler, texture2d_array<float> Chroma)
{
    float luma = Luma.sample(Sampler, uv).x;
    float3 _44 = float3(uv, 0.0);
    float cb = Chroma.sample(Sampler, _44.xy, uint(rint(_44.z))).x;
    float3 _55 = float3(uv, 1.0);
    float cr = Chroma.sample(Sampler, _55.xy, uint(rint(_55.z))).x;
    float4 yuva = float4(luma, cb, cr, 1.0);
    float4 rgba = yuva * float4x4(float4(1.16437995433807373046875, 0.0, 1.5960299968719482421875, -0.870790004730224609375), float4(1.16437995433807373046875, -0.39175999164581298828125, -0.812969982624053955078125, 0.52959001064300537109375), float4(1.16437995433807373046875, 2.01723003387451171875, 0.0, -1.08139002323150634765625), float4(0.0, 0.0, 0.0, 1.0));
    return rgba;
}

fragment main0_out main0(main0_in in [[stage_in]], constant EnableAlpha& _86 [[buffer(1)]], texture2d<float> Luma [[texture(0)]], texture2d_array<float> Chroma [[texture(1)]], sampler Sampler [[sampler(0)]])
{
    main0_out out = {};
    float4 rgba;
    if (!(_86._EnableAlpha != 0u))
    {
        float2 param = float2(in.fs_TexCoord.x, in.fs_TexCoord.y);
        rgba = sampleTex(param, Luma, Sampler, Chroma);
    }
    else
    {
        float2 param_1 = float2(in.fs_TexCoord.x, in.fs_TexCoord.y / 2.0);
        rgba = sampleTex(param_1, Luma, Sampler, Chroma);
        float2 param_2 = float2(in.fs_TexCoord.x, (in.fs_TexCoord.y / 2.0) + 0.5);
        float alpha = sampleTex(param_2, Luma, Sampler, Chroma).x;
        rgba.w = (alpha >= 0.89999997615814208984375) ? 1.0 : alpha;
    }
    rgba *= in.fs_Color.w;
    out.OutColor = rgba;
    return out;
}
