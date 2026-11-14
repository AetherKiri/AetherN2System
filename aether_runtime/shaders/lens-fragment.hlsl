Texture2D<float4> Texture : register(t0);
SamplerState Sampler : register(s0);
Texture2D<float4> LensTexture : register(t1);

static float4 gl_FragCoord;
static float2 fs_TexCoord;
static float4 OutColor;
static float4 fs_Color;

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

float4 sample_input(float2 uv, float2 offset, float2 texelSize)
{
    return Texture.Sample(Sampler, uv + (offset * texelSize));
}

void frag_main()
{
    float4 mapTexel = LensTexture.Sample(Sampler, fs_TexCoord);
    float2 offset = mapTexel.yx;
    float2 fRte = mapTexel.wz;
    if (mapTexel.w == 1.0f)
    {
        offset.x = -offset.x;
    }
    if (mapTexel.z == 1.0f)
    {
        offset.y = -offset.y;
    }
    uint _81_dummy_parameter;
    float2 srcTexSize = float2(int2(spvTextureSize(Texture, uint(0), _81_dummy_parameter)));
    float2 texelSize = 1.0f.xx / srcTexSize;
    float2 uvOffset = (offset * 128.0f) / srcTexSize;
    float2 uv = (gl_FragCoord.xy / srcTexSize) + uvOffset;
    float2 param = uv;
    float2 param_1 = 0.0f.xx;
    float2 param_2 = texelSize;
    float4 t0 = sample_input(param, param_1, param_2);
    float2 param_3 = uv;
    float2 param_4 = float2(1.0f, 0.0f);
    float2 param_5 = texelSize;
    float4 t1 = sample_input(param_3, param_4, param_5);
    float2 param_6 = uv;
    float2 param_7 = float2(0.0f, 1.0f);
    float2 param_8 = texelSize;
    float4 t2 = sample_input(param_6, param_7, param_8);
    float2 param_9 = uv;
    float2 param_10 = 1.0f.xx;
    float2 param_11 = texelSize;
    float4 t3 = sample_input(param_9, param_10, param_11);
    OutColor = lerp(lerp(t0, t1, fRte.x.xxxx), lerp(t2, t3, fRte.x.xxxx), fRte.y.xxxx);
    OutColor.w = 1.0f;
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
