#pragma clang diagnostic ignored "-Wmissing-prototypes"
#pragma clang diagnostic ignored "-Wmissing-braces"

#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

template<typename T, size_t Num>
struct spvUnsafeArray
{
    T elements[Num ? Num : 1];

    thread T& operator [] (size_t pos) thread
    {
        return elements[pos];
    }
    constexpr const thread T& operator [] (size_t pos) const thread
    {
        return elements[pos];
    }

    device T& operator [] (size_t pos) device
    {
        return elements[pos];
    }
    constexpr const device T& operator [] (size_t pos) const device
    {
        return elements[pos];
    }

    constexpr const constant T& operator [] (size_t pos) const constant
    {
        return elements[pos];
    }

    threadgroup T& operator [] (size_t pos) threadgroup
    {
        return elements[pos];
    }
    constexpr const threadgroup T& operator [] (size_t pos) const threadgroup
    {
        return elements[pos];
    }
};

// Implementation of signed integer mod accurate to SPIR-V specification
template<typename Tx, typename Ty>
inline Tx spvSMod(Tx x, Ty y)
{
    Tx remainder = x - y * (x / y);
    return select(Tx(remainder + y), remainder, remainder == 0 || (x >= 0) == (y >= 0));
}

struct ViewProjection
{
    float4x4 _ViewProjection;
};

constant spvUnsafeArray<uint, 6> _250 = spvUnsafeArray<uint, 6>({ 0u, 1u, 2u, 2u, 1u, 3u });

struct main0_out
{
    float4 fs_Color [[user(locn0)]];
    float3 fs_TexCoord [[user(locn1)]];
    float4 gl_Position [[position]];
};

struct main0_in
{
    float2 vs_Offset [[attribute(0)]];
    int vs_GlyphRunID [[attribute(1)]];
    int vs_GlyphID [[attribute(2)]];
    float vs_Opacity [[attribute(4)]];
};

static inline __attribute__((always_inline))
int2 getCacheEntryUV(thread const int& slot, thread const int& blocksPerSlot, thread const int& cacheTexSize)
{
    return int2(spvSMod(slot * blocksPerSlot, cacheTexSize), (slot * blocksPerSlot) / cacheTexSize);
}

vertex main0_out main0(main0_in in [[stage_in]], constant ViewProjection& _267 [[buffer(0)]], texture2d<float> GlyphRuns [[texture(0)]], texture2d<float> Transforms [[texture(1)]], texture2d<float> GlyphRects [[texture(2)]], uint gl_VertexIndex [[vertex_id]])
{
    main0_out out = {};
    int glyphRunTexSize = int2(GlyphRuns.get_width(), GlyphRuns.get_height()).x;
    int transformTexSize = int2(Transforms.get_width(), Transforms.get_height()).x;
    int glyphRectTexSize = int2(GlyphRects.get_width(), GlyphRects.get_height()).x;
    int param = in.vs_GlyphRunID;
    int param_1 = 2;
    int param_2 = glyphRunTexSize;
    int2 glyphRunUV = getCacheEntryUV(param, param_1, param_2);
    float4 textColor = GlyphRuns.read(uint2(glyphRunUV), 0);
    float4 outlineColor = GlyphRuns.read(uint2((glyphRunUV + int2(1, 0))), 0);
    int param_3 = in.vs_GlyphRunID;
    int param_4 = 4;
    int param_5 = transformTexSize;
    int2 transformUV = getCacheEntryUV(param_3, param_4, param_5);
    float4 col1 = Transforms.read(uint2(transformUV), 0);
    float4 col2 = Transforms.read(uint2((transformUV + int2(1, 0))), 0);
    float4 col3 = Transforms.read(uint2((transformUV + int2(2, 0))), 0);
    float4 col4 = Transforms.read(uint2((transformUV + int2(3, 0))), 0);
    float4x4 transform = float4x4(float4(col1), float4(col2), float4(col3), float4(col4));
    int param_6 = in.vs_GlyphID;
    int param_7 = 2;
    int param_8 = glyphRectTexSize;
    int2 glyphInfoUV = getCacheEntryUV(param_6, param_7, param_8);
    float4 block1 = GlyphRects.read(uint2(glyphInfoUV), 0);
    float4 block2 = GlyphRects.read(uint2((glyphInfoUV + int2(1, 0))), 0);
    float layer = block1.x;
    float2 texOrigin = block1.yz;
    float2 size = float2(block1.w, block2.x);
    float4 uvRect = float4(texOrigin.x, texOrigin.y, texOrigin.x + size.x, texOrigin.y + size.y);
    spvUnsafeArray<float2, 4> _222 = spvUnsafeArray<float2, 4>({ float2(in.vs_Offset.x, in.vs_Offset.y), float2(in.vs_Offset.x + size.x, in.vs_Offset.y), float2(in.vs_Offset.x, in.vs_Offset.y + size.y), float2(in.vs_Offset.x + size.x, in.vs_Offset.y + size.y) });
    spvUnsafeArray<float2, 4> quad = _222;
    spvUnsafeArray<float2, 4> _245 = spvUnsafeArray<float2, 4>({ float2(uvRect.x, uvRect.y), float2(uvRect.z, uvRect.y), float2(uvRect.x, uvRect.w), float2(uvRect.z, uvRect.w) });
    spvUnsafeArray<float2, 4> uv = _245;
    uint index = _250[int(gl_VertexIndex)];
    float2 vert = quad[index];
    out.gl_Position = (_267._ViewProjection * transform) * float4(vert.x, vert.y, 0.0, 1.0);
    out.fs_TexCoord = float3(uv[index] / float2(512.0), layer);
    out.fs_Color = textColor * in.vs_Opacity;
    return out;
}
