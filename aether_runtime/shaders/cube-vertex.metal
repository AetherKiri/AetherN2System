#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct ViewProjection
{
    float4x4 _ViewProjection;
};

struct World
{
    float4x4 _World;
};

struct main0_out
{
    float3 fs_TexCoord [[user(locn0)]];
    float fs_Opacity [[user(locn1)]];
    float4 gl_Position [[position]];
};

struct main0_in
{
    float3 vs_Position [[attribute(0)]];
    float vs_Opacity [[attribute(1)]];
};

vertex main0_out main0(main0_in in [[stage_in]], constant ViewProjection& _39 [[buffer(0)]], constant World& _45 [[buffer(1)]])
{
    main0_out out = {};
    out.fs_TexCoord = float3(-in.vs_Position.x, in.vs_Position.y, in.vs_Position.z);
    out.fs_Opacity = in.vs_Opacity;
    out.gl_Position = (_39._ViewProjection * _45._World) * float4(in.vs_Position, 1.0);
    return out;
}
