cbuffer ViewProjection : register(b0)
{
    row_major float4x4 _19_ViewProjection : packoffset(c0);
};


static float4 gl_Position;
static float2 vs_Position;
static float4 fs_Color;
static float4 vs_Color;
static float2 fs_TexCoord;
static float2 vs_TexCoord;

struct SPIRV_Cross_Input
{
    float2 vs_Position : TEXCOORD0;
    float2 vs_TexCoord : TEXCOORD1;
    float4 vs_Color : TEXCOORD2;
};

struct SPIRV_Cross_Output
{
    float4 fs_Color : TEXCOORD0;
    float2 fs_TexCoord : TEXCOORD1;
    float4 gl_Position : SV_Position;
};

void vert_main()
{
    gl_Position = mul(float4(vs_Position, 0.0f, 1.0f), _19_ViewProjection);
    fs_Color = vs_Color;
    fs_TexCoord = vs_TexCoord;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    vs_Position = stage_input.vs_Position;
    vs_Color = stage_input.vs_Color;
    vs_TexCoord = stage_input.vs_TexCoord;
    vert_main();
    SPIRV_Cross_Output stage_output;
    stage_output.gl_Position = gl_Position;
    stage_output.fs_Color = fs_Color;
    stage_output.fs_TexCoord = fs_TexCoord;
    return stage_output;
}
