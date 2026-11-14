#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct ViewProjection
{
    float4x4 _ViewProjection;
};

struct main0_out
{
    float3 fs_TexCoord [[user(locn0)]];
    float4 gl_Position [[position]];
};

struct main0_in
{
    float2 vs_Position [[attribute(0)]];
    float3 vs_TexCoord [[attribute(1)]];
};

vertex main0_out main0(main0_in in [[stage_in]], constant ViewProjection& _19 [[buffer(0)]])
{
    main0_out out = {};
    out.gl_Position = _19._ViewProjection * float4(in.vs_Position, 0.0, 1.0);
    out.fs_TexCoord = in.vs_TexCoord;
    return out;
}
