#pragma clang diagnostic ignored "-Wmissing-prototypes"

#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct main0_out
{
    float4 OutColor [[color(0)]];
};

struct main0_in
{
    float2 fs_TexCoord [[user(locn1)]];
};

static inline __attribute__((always_inline))
float4 sample_input(thread const float2& uv, thread const float2& offset, thread const float2& texelSize, texture2d<float> Texture, sampler Sampler)
{
    return Texture.sample(Sampler, (uv + (offset * texelSize)));
}

fragment main0_out main0(main0_in in [[stage_in]], texture2d<float> Texture [[texture(0)]], texture2d<float> LensTexture [[texture(1)]], sampler Sampler [[sampler(0)]], float4 gl_FragCoord [[position]])
{
    main0_out out = {};
    float4 mapTexel = LensTexture.sample(Sampler, in.fs_TexCoord);
    float2 offset = mapTexel.yx;
    float2 fRte = mapTexel.wz;
    if (mapTexel.w == 1.0)
    {
        offset.x = -offset.x;
    }
    if (mapTexel.z == 1.0)
    {
        offset.y = -offset.y;
    }
    float2 srcTexSize = float2(int2(Texture.get_width(), Texture.get_height()));
    float2 texelSize = float2(1.0) / srcTexSize;
    float2 uvOffset = (offset * 128.0) / srcTexSize;
    float2 uv = (gl_FragCoord.xy / srcTexSize) + uvOffset;
    float2 param = uv;
    float2 param_1 = float2(0.0);
    float2 param_2 = texelSize;
    float4 t0 = sample_input(param, param_1, param_2, Texture, Sampler);
    float2 param_3 = uv;
    float2 param_4 = float2(1.0, 0.0);
    float2 param_5 = texelSize;
    float4 t1 = sample_input(param_3, param_4, param_5, Texture, Sampler);
    float2 param_6 = uv;
    float2 param_7 = float2(0.0, 1.0);
    float2 param_8 = texelSize;
    float4 t2 = sample_input(param_6, param_7, param_8, Texture, Sampler);
    float2 param_9 = uv;
    float2 param_10 = float2(1.0);
    float2 param_11 = texelSize;
    float4 t3 = sample_input(param_9, param_10, param_11, Texture, Sampler);
    out.OutColor = mix(mix(t0, t1, float4(fRte.x)), mix(t2, t3, float4(fRte.x)), float4(fRte.y));
    out.OutColor.w = 1.0;
    return out;
}
