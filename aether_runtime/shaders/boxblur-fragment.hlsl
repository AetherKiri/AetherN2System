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

uint2 spvTextureSize(Texture2D<float4> Tex, uint Level, out uint Param)
{
    uint2 ret;
    Tex.GetDimensions(Level, ret.x, ret.y, Param);
    return ret;
}

float4 texel(float2 offset, float2 texelSize)
{
    return Input.Sample(Sampler, fs_TexCoord + (offset * texelSize));
}

void frag_main()
{
    uint _42_dummy_parameter;
    float2 texelSize = 1.0f.xx / float2(int2(spvTextureSize(Input, uint(0), _42_dummy_parameter)));
    float2 param = (-1.0f).xx;
    float2 param_1 = texelSize;
    float2 param_2 = float2(0.0f, -1.0f);
    float2 param_3 = texelSize;
    float2 param_4 = float2(1.0f, -1.0f);
    float2 param_5 = texelSize;
    float2 param_6 = float2(-1.0f, 0.0f);
    float2 param_7 = texelSize;
    float2 param_8 = 0.0f.xx;
    float2 param_9 = texelSize;
    float2 param_10 = float2(1.0f, 0.0f);
    float2 param_11 = texelSize;
    float2 param_12 = float2(-1.0f, 1.0f);
    float2 param_13 = texelSize;
    float2 param_14 = float2(0.0f, 1.0f);
    float2 param_15 = texelSize;
    float2 param_16 = 1.0f.xx;
    float2 param_17 = texelSize;
    float4 sum = (((((((texel(param, param_1) + texel(param_2, param_3)) + texel(param_4, param_5)) + texel(param_6, param_7)) + texel(param_8, param_9)) + texel(param_10, param_11)) + texel(param_12, param_13)) + texel(param_14, param_15)) + texel(param_16, param_17);
    OutColor = (sum / 9.0f.xxxx) * 0.9900000095367431640625f;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    fs_TexCoord = stage_input.fs_TexCoord;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.OutColor = OutColor;
    return stage_output;
}
