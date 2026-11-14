#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct main0_out
{
    float4 OutColor [[color(0)]];
};

struct main0_in
{
    float4 fs_Color [[user(locn0)]];
    float3 fs_TexCoord [[user(locn1)]];
};

fragment main0_out main0(main0_in in [[stage_in]], texture2d_array<float> CacheTexture [[texture(3)]], sampler Sampler [[sampler(0)]])
{
    main0_out out = {};
    float alpha = CacheTexture.sample(Sampler, in.fs_TexCoord.xy, uint(rint(in.fs_TexCoord.z))).x * in.fs_Color.w;
    out.OutColor = float4(in.fs_Color.x, in.fs_Color.y, in.fs_Color.z, alpha);
    return out;
}
