cbuffer FadeAmount : register(b1)
{
    float _35_FadeAmount : packoffset(c0);
};

Texture2D<float4> Mask : register(t1);
SamplerState Sampler : register(s0);
Texture2D<float4> Texture : register(t0);

static float2 fs_TexCoord;
static float4 OutColor;
static float4 fs_Color;

struct SPIRV_Cross_Input
{
    float4 fs_Color : TEXCOORD0;
    float2 fs_TexCoord : TEXCOORD1;
};

struct SPIRV_Cross_Output
{
    float4 OutColor : SV_Target0;
};

void frag_main()
{
    float mask = (Mask.Sample(Sampler, fs_TexCoord).x * 0.89999997615814208984375f) + 0.0500000007450580596923828125f;
    float alpha = clamp(((_35_FadeAmount - mask) / 0.100000001490116119384765625f) + 0.5f, 0.0f, 1.0f);
    OutColor = (Texture.Sample(Sampler, fs_TexCoord) * fs_Color) * alpha;
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
