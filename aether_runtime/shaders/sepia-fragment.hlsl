Texture2D<float4> Input : register(t0);
SamplerState Sampler : register(s0);

static float2 fs_TexCoord;
static float4 OutColor;

struct SPIRV_Cross_Input
{
    float2 fs_TexCoord : TEXCOORD0;
};

struct SPIRV_Cross_Output
{
    float4 OutColor : SV_Target0;
};

void frag_main()
{
    float4 texel = Input.Sample(Sampler, fs_TexCoord);
    uint3 rgb = uint3((texel.xyz * 255.0f) + 0.5f.xxx);
    uint3 terms = (rgb * uint3(28u, 151u, 77u)) >> uint3(int3(8, 8, 8));
    uint gray = (terms.x + terms.y) + terms.z;
    uint warm = min((gray + 30u), 255u);
    OutColor = float4(float3(float(warm), float(warm), float(gray)) / 255.0f.xxx, texel.w);
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    fs_TexCoord = stage_input.fs_TexCoord;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.OutColor = OutColor;
    return stage_output;
}
