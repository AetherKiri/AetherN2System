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
    float4 c = CacheTexture.sample(Sampler, in.fs_TexCoord.xy, uint(rint(in.fs_TexCoord.z)));
    float alpha = c.y;
    alpha = mix(alpha, c.y, 0.60000002384185791015625);
    alpha = mix(alpha, c.z, 0.300000011920928955078125);
    alpha = mix(alpha, c.w, 0.1500000059604644775390625);
    out.OutColor = in.fs_Color * alpha;
    return out;
}
