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

fragment main0_out main0(main0_in in [[stage_in]], texture2d<float> Input [[texture(0)]], sampler Sampler [[sampler(0)]])
{
    main0_out out = {};
    float4 texel = Input.sample(Sampler, in.fs_TexCoord);
    uint3 rgb = uint3((texel.xyz * 255.0) + float3(0.5));
    uint3 terms = (rgb * uint3(28u, 151u, 77u)) >> uint3(int3(8));
    uint gray = (terms.x + terms.y) + terms.z;
    uint warm = min((gray + 30u), 255u);
    out.OutColor = float4(float3(float(warm), float(warm), float(gray)) / float3(255.0), texel.w);
    return out;
}
