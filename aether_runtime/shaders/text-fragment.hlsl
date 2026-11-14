Texture2DArray<float4> CacheTexture : register(t3);
SamplerState Sampler : register(s0);

static float3 fs_TexCoord;
static float4 fs_Color;
static float4 OutColor;

struct SPIRV_Cross_Input
{
    float4 fs_Color : TEXCOORD0;
    float3 fs_TexCoord : TEXCOORD1;
};

struct SPIRV_Cross_Output
{
    float4 OutColor : SV_Target0;
};

void frag_main()
{
    float alpha = CacheTexture.Sample(Sampler, fs_TexCoord).x * fs_Color.w;
    OutColor = float4(fs_Color.x, fs_Color.y, fs_Color.z, alpha);
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
