cbuffer TransitionParameters : register(b1)
{
    float4 _20_Amounts : packoffset(c0);
    float4 _20_Viewport : packoffset(c1);
    float4 _20_Coverage[64] : packoffset(c2);
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
    uint _40_dummy_parameter;
    float2 maskUV = ((gl_FragCoord.xy - _20_Viewport.xy) / _20_Amounts.z.xx) / float2(int2(spvTextureSize(Mask, uint(0), _40_dummy_parameter)));
    float mask = Mask.Sample(Sampler, maskUV).x;
    float feather = _20_Amounts.y;
    float threshold = (mask * (1.0f - feather)) + (feather * 0.5f);
    float _75;
    if (feather > 0.0f)
    {
        _75 = clamp(((_20_Amounts.x - threshold) / feather) + 0.5f, 0.0f, 1.0f);
    }
    else
    {
        _75 = step(threshold, _20_Amounts.x);
    }
    float coverage = _75;
    if (_20_Amounts.x <= 0.0f)
    {
        coverage = 0.0f;
    }
    if (_20_Amounts.x >= 1.0f)
    {
        coverage = 1.0f;
    }
    if (_20_Amounts.w > 0.0f)
    {
        int _114;
        if (_20_Amounts.w >= 2.0f)
        {
            _114 = int(round(clamp(mask, 0.0f, 1.0f) * 255.0f));
        }
        else
        {
            _114 = 0;
        }
        int index = _114;
        coverage = _20_Coverage[index / 4][index % 4];
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
