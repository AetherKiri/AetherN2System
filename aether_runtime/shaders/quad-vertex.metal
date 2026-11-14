#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct ViewProjection
{
    float4x4 _ViewProjection;
};

struct main0_out
{
    float4 fs_Color [[user(locn0)]];
    float2 fs_TexCoord [[user(locn1)]];
    float4 gl_Position [[position]];
};

struct main0_in
{
    float2 vs_Position [[attribute(0)]];
    float2 vs_TexCoord [[attribute(1)]];
    float4 vs_Color [[attribute(2)]];
};

vertex main0_out main0(main0_in in [[stage_in]], constant ViewProjection& _19 [[buffer(0)]])
{
    main0_out out = {};
    out.gl_Position = _19._ViewProjection * float4(in.vs_Position, 0.0, 1.0);
    out.fs_Color = in.vs_Color;
    out.fs_TexCoord = in.vs_TexCoord;
    return out;
}
