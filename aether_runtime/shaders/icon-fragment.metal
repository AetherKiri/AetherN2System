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
};

fragment main0_out main0(main0_in in [[stage_in]], texture2d_array<float> Texture [[texture(0)]], sampler Sampler [[sampler(0)]])
{
    main0_out out = {};
    out.OutColor = Texture.sample(Sampler, in.fs_TexCoord.xy, uint(rint(in.fs_TexCoord.z)));
    return out;
}
