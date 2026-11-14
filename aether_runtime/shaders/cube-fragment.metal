#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct main0_out
{
    float4 OutColor [[color(0)]];
};

struct main0_in
{
    float3 fs_TexCoord [[user(locn0)]];
    float fs_Opacity [[user(locn1)]];
};

fragment main0_out main0(main0_in in [[stage_in]], texturecube<float> Texture [[texture(0)]], sampler Sampler [[sampler(0)]])
{
    main0_out out = {};
    out.OutColor = Texture.sample(Sampler, in.fs_TexCoord) * in.fs_Opacity;
    return out;
}
