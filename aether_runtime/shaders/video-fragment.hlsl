cbuffer EnableAlpha : register(b1)
{
    uint _86_EnableAlpha : packoffset(c0);
    float3 _86_padding : packoffset(c1);
};

Texture2D<float4> Luma : register(t0);
SamplerState Sampler : register(s0);
Texture2DArray<float4> Chroma : register(t1);

static float2 fs_TexCoord;
static float4 fs_Color;
static float4 OutColor;

struct SPIRV_Cross_Input
{
    float4 fs_Color : TEXCOORD0;
    float2 fs_TexCoord : TEXCOORD1;
};

struct SPIRV_Cross_Output
{
    float4 OutColor : SV_Target0;
};

float4 sampleTex(float2 uv)
{
    float luma = Luma.Sample(Sampler, uv).x;
    float cb = Chroma.Sample(Sampler, float3(uv, 0.0f)).x;
    float cr = Chroma.Sample(Sampler, float3(uv, 1.0f)).x;
    float4 yuva = float4(luma, cb, cr, 1.0f);
    float4 rgba = mul(float4x4(float4(1.16437995433807373046875f, 0.0f, 1.5960299968719482421875f, -0.870790004730224609375f), float4(1.16437995433807373046875f, -0.39175999164581298828125f, -0.812969982624053955078125f, 0.52959001064300537109375f), float4(1.16437995433807373046875f, 2.01723003387451171875f, 0.0f, -1.08139002323150634765625f), float4(0.0f, 0.0f, 0.0f, 1.0f)), yuva);
    return rgba;
}

void frag_main()
{
    float4 rgba;
    if (!(_86_EnableAlpha != 0u))
    {
        float2 param = float2(fs_TexCoord.x, fs_TexCoord.y);
        rgba = sampleTex(param);
    }
    else
    {
        float2 param_1 = float2(fs_TexCoord.x, fs_TexCoord.y / 2.0f);
        rgba = sampleTex(param_1);
        float2 param_2 = float2(fs_TexCoord.x, (fs_TexCoord.y / 2.0f) + 0.5f);
        float alpha = sampleTex(param_2).x;
        rgba.w = (alpha >= 0.89999997615814208984375f) ? 1.0f : alpha;
    }
    rgba *= fs_Color.w;
    OutColor = rgba;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    fs_TexCoord = stage_input.fs_TexCoord;
    fs_Color = stage_input.fs_Color;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.OutColor = OutColor;
    return stage_output;
}
