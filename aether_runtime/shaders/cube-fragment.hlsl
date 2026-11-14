TextureCube<float4> Texture : register(t0);
SamplerState Sampler : register(s0);

static float4 OutColor;
static float3 fs_TexCoord;
static float fs_Opacity;

struct SPIRV_Cross_Input
{
    float3 fs_TexCoord : TEXCOORD0;
    float fs_Opacity : TEXCOORD1;
};

struct SPIRV_Cross_Output
{
    float4 OutColor : SV_Target0;
};

void frag_main()
{
    OutColor = Texture.Sample(Sampler, fs_TexCoord) * fs_Opacity;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    fs_TexCoord = stage_input.fs_TexCoord;
    fs_Opacity = stage_input.fs_Opacity;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.OutColor = OutColor;
    return stage_output;
}
