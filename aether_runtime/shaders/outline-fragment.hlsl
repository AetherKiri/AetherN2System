Texture2DArray<float4> CacheTexture : register(t3);
SamplerState Sampler : register(s0);

static float3 fs_TexCoord;
static float4 OutColor;
static float4 fs_Color;

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
    float4 c = CacheTexture.Sample(Sampler, fs_TexCoord);
    float alpha = c.y;
    alpha = lerp(alpha, c.y, 0.60000002384185791015625f);
    alpha = lerp(alpha, c.z, 0.300000011920928955078125f);
    alpha = lerp(alpha, c.w, 0.1500000059604644775390625f);
    OutColor = fs_Color * alpha;
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
