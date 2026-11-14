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
    float2 fs_TexCoord [[user(locn0)]];
};

static inline __attribute__((always_inline))
float4 texel(thread const float2& offset, thread const float2& texelSize, texture2d<float> Input, sampler Sampler, thread float2& fs_TexCoord)
{
    return Input.sample(Sampler, (fs_TexCoord + (offset * texelSize)));
}

fragment main0_out main0(main0_in in [[stage_in]], texture2d<float> Input [[texture(0)]], sampler Sampler [[sampler(0)]])
{
    main0_out out = {};
    float2 texelSize = float2(1.0) / float2(int2(Input.get_width(), Input.get_height()));
    float2 param = float2(-1.0);
    float2 param_1 = texelSize;
    float2 param_2 = float2(0.0, -1.0);
    float2 param_3 = texelSize;
    float2 param_4 = float2(1.0, -1.0);
    float2 param_5 = texelSize;
    float2 param_6 = float2(-1.0, 0.0);
    float2 param_7 = texelSize;
    float2 param_8 = float2(0.0);
    float2 param_9 = texelSize;
    float2 param_10 = float2(1.0, 0.0);
    float2 param_11 = texelSize;
    float2 param_12 = float2(-1.0, 1.0);
    float2 param_13 = texelSize;
    float2 param_14 = float2(0.0, 1.0);
    float2 param_15 = texelSize;
    float2 param_16 = float2(1.0);
    float2 param_17 = texelSize;
    float4 sum = (((((((texel(param, param_1, Input, Sampler, in.fs_TexCoord) + texel(param_2, param_3, Input, Sampler, in.fs_TexCoord)) + texel(param_4, param_5, Input, Sampler, in.fs_TexCoord)) + texel(param_6, param_7, Input, Sampler, in.fs_TexCoord)) + texel(param_8, param_9, Input, Sampler, in.fs_TexCoord)) + texel(param_10, param_11, Input, Sampler, in.fs_TexCoord)) + texel(param_12, param_13, Input, Sampler, in.fs_TexCoord)) + texel(param_14, param_15, Input, Sampler, in.fs_TexCoord)) + texel(param_16, param_17, Input, Sampler, in.fs_TexCoord);
    out.OutColor = (sum / float4(9.0)) * 0.9900000095367431640625;
    return out;
}
