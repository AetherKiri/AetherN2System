Texture2DArray<float4> Texture : register(t0);
SamplerState Sampler : register(s0);

static float4 OutColor;
static float3 fs_TexCoord;

struct SPIRV_Cross_Input
{
    float3 fs_TexCoord : TEXCOORD0;
};

struct SPIRV_Cross_Output
{
    float4 OutColor : SV_Target0;
};

void frag_main()
{
    OutColor = Texture.Sample(Sampler, fs_TexCoord);
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    fs_TexCoord = stage_input.fs_TexCoord;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.OutColor = OutColor;
    return stage_output;
}
