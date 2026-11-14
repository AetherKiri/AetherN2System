cbuffer AlphaMaskPos : register(b1)
{
    float2 _33_AlphaMaskPos : packoffset(c0);
    float2 _33_padding : packoffset(c0.z);
};

Texture2D<float4> Texture : register(t0);
SamplerState Sampler : register(s0);
Texture2D<float4> AlphaMask : register(t1);

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
    float4 texel = Texture.Sample(Sampler, fs_TexCoord);
    uint _43_dummy_parameter;
    float2 maskUV = (gl_FragCoord.xy - _33_AlphaMaskPos) / float2(int2(spvTextureSize(AlphaMask, uint(0), _43_dummy_parameter)));
    float mask = AlphaMask.Sample(Sampler, maskUV).x;
    float alpha = (texel.w * fs_Color.w) * mask;
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
