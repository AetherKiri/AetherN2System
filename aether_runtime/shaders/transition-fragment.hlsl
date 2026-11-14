cbuffer TransitionParameters : register(b1)
{
    float4 _17_Amounts : packoffset(c0);
    float4 _17_Viewport : packoffset(c1);
};

Texture2D<float4> Mask : register(t1);
SamplerState Sampler : register(s0);
Texture2D<float4> Texture : register(t0);

static float4 gl_FragCoord;
static float2 fs_TexCoord;
static float4 fs_Color;
static float4 OutColor;

struct SPIRV_Cross_Input
{
    float4 fs_Color : TEXCOORD0;
    float2 fs_TexCoord : TEXCOORD1;
    float4 gl_FragCoord : SV_Position;
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

void frag_main()
{
    uint _38_dummy_parameter;
    float2 maskUV = ((gl_FragCoord.xy - _17_Viewport.xy) / _17_Amounts.z.xx) / float2(int2(spvTextureSize(Mask, uint(0), _38_dummy_parameter)));
    float mask = Mask.Sample(Sampler, maskUV).x;
    float feather = _17_Amounts.y;
    float threshold = (mask * (1.0f - feather)) + (feather * 0.5f);
    float _73;
    if (feather > 0.0f)
    {
        _73 = clamp(((_17_Amounts.x - threshold) / feather) + 0.5f, 0.0f, 1.0f);
    }
    else
    {
        _73 = step(threshold, _17_Amounts.x);
    }
    float coverage = _73;
    if (_17_Amounts.x <= 0.0f)
    {
        coverage = 0.0f;
    }
    if (_17_Amounts.x >= 1.0f)
    {
        coverage = 1.0f;
    }
    float4 texel = Texture.Sample(Sampler, fs_TexCoord);
    float alpha = (texel.w * fs_Color.w) * coverage;
    OutColor = float4(((texel * fs_Color) * alpha).xyz, alpha);
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    gl_FragCoord = stage_input.gl_FragCoord;
    gl_FragCoord.w = 1.0 / gl_FragCoord.w;
    fs_TexCoord = stage_input.fs_TexCoord;
    fs_Color = stage_input.fs_Color;
    frag_main();
    SPIRV_Cross_Output stage_output;
    stage_output.OutColor = OutColor;
    return stage_output;
}
