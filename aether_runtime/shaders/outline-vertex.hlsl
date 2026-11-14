static const uint _256[6] = { 0u, 1u, 2u, 2u, 1u, 3u };

cbuffer ViewProjection : register(b0)
{
    row_major float4x4 _273_ViewProjection : packoffset(c0);
};

Texture2D<float4> GlyphRuns : register(t0);
Texture2D<float4> Transforms : register(t1);
Texture2D<float4> GlyphRects : register(t2);

static float4 gl_Position;
static int gl_VertexIndex;
static int vs_GlyphRunID;
static int vs_OutlineID;
static float2 vs_Offset;
static float3 fs_TexCoord;
static float4 fs_Color;
static float vs_Opacity;
static int vs_GlyphID;

struct SPIRV_Cross_Input
{
    float2 vs_Offset : TEXCOORD0;
    int vs_GlyphRunID : TEXCOORD1;
    int vs_GlyphID : TEXCOORD2;
    int vs_OutlineID : TEXCOORD3;
    float vs_Opacity : TEXCOORD4;
    uint gl_VertexIndex : SV_VertexID;
};

struct SPIRV_Cross_Output
{
    float4 fs_Color : TEXCOORD0;
    float3 fs_TexCoord : TEXCOORD1;
    float4 gl_Position : SV_Position;
};

uint2 spvTextureSize(Texture2D<float4> Tex, uint Level, out uint Param)
{
    uint2 ret;
    Tex.GetDimensions(Level, ret.x, ret.y, Param);
    return ret;
}

int2 getCacheEntryUV(int slot, int blocksPerSlot, int cacheTexSize)
{
    return int2((slot * blocksPerSlot) % cacheTexSize, (slot * blocksPerSlot) / cacheTexSize);
}

void vert_main()
{
    uint _35_dummy_parameter;
    int glyphRunTexSize = int2(spvTextureSize(GlyphRuns, uint(0), _35_dummy_parameter)).x;
    uint _42_dummy_parameter;
    int transformTexSize = int2(spvTextureSize(Transforms, uint(0), _42_dummy_parameter)).x;
    uint _47_dummy_parameter;
    int glyphRectTexSize = int2(spvTextureSize(GlyphRects, uint(0), _47_dummy_parameter)).x;
    int param = vs_GlyphRunID;
    int param_1 = 2;
    int param_2 = glyphRunTexSize;
    int2 glyphRunUV = getCacheEntryUV(param, param_1, param_2);
    float4 textColor = GlyphRuns.Load(int3(glyphRunUV, 0));
    float4 outlineColor = GlyphRuns.Load(int3(glyphRunUV + int2(1, 0), 0));
    int param_3 = vs_GlyphRunID;
    int param_4 = 4;
    int param_5 = transformTexSize;
    int2 transformUV = getCacheEntryUV(param_3, param_4, param_5);
    float4 col1 = Transforms.Load(int3(transformUV, 0));
    float4 col2 = Transforms.Load(int3(transformUV + int2(1, 0), 0));
    float4 col3 = Transforms.Load(int3(transformUV + int2(2, 0), 0));
    float4 col4 = Transforms.Load(int3(transformUV + int2(3, 0), 0));
    float4x4 transform = float4x4(float4(col1), float4(col2), float4(col3), float4(col4));
    int param_6 = vs_OutlineID;
    int param_7 = 2;
    int param_8 = glyphRectTexSize;
    int2 glyphInfoUV = getCacheEntryUV(param_6, param_7, param_8);
    float4 block1 = GlyphRects.Load(int3(glyphInfoUV, 0));
    float4 block2 = GlyphRects.Load(int3(glyphInfoUV + int2(1, 0), 0));
    float layer = block1.x;
    float2 texOrigin = block1.yz;
    float2 size = float2(block1.w, block2.x);
    float2 outlineOffset = block2.yz;
    float4 uvRect = float4(texOrigin.x, texOrigin.y, texOrigin.x + size.x, texOrigin.y + size.y);
    float2 offset = vs_Offset + outlineOffset;
    float2 _228[4] = { float2(offset.x, offset.y), float2(offset.x + size.x, offset.y), float2(offset.x, offset.y + size.y), float2(offset.x + size.x, offset.y + size.y) };
    float2 quad[4] = _228;
    float2 _251[4] = { float2(uvRect.x, uvRect.y), float2(uvRect.z, uvRect.y), float2(uvRect.x, uvRect.w), float2(uvRect.z, uvRect.w) };
    float2 uv[4] = _251;
    uint index = _256[gl_VertexIndex];
    float2 vert = quad[index];
    gl_Position = mul(float4(vert.x, vert.y, 0.0f, 1.0f), mul(transform, _273_ViewProjection));
    fs_TexCoord = float3(uv[index] / 512.0f.xx, layer);
    fs_Color = outlineColor * vs_Opacity;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    gl_VertexIndex = int(stage_input.gl_VertexIndex);
    vs_GlyphRunID = stage_input.vs_GlyphRunID;
    vs_OutlineID = stage_input.vs_OutlineID;
    vs_Offset = stage_input.vs_Offset;
    vs_Opacity = stage_input.vs_Opacity;
    vs_GlyphID = stage_input.vs_GlyphID;
    vert_main();
    SPIRV_Cross_Output stage_output;
    stage_output.gl_Position = gl_Position;
    stage_output.fs_TexCoord = fs_TexCoord;
    stage_output.fs_Color = fs_Color;
    return stage_output;
}
