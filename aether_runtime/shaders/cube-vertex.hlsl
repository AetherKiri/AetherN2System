cbuffer ViewProjection : register(b0)
{
    row_major float4x4 _39_ViewProjection : packoffset(c0);
};

cbuffer World : register(b1)
{
    row_major float4x4 _45_World : packoffset(c0);
};


static float4 gl_Position;
static float3 fs_TexCoord;
static float3 vs_Position;
static float fs_Opacity;
static float vs_Opacity;

struct SPIRV_Cross_Input
{
    float3 vs_Position : TEXCOORD0;
    float vs_Opacity : TEXCOORD1;
};

struct SPIRV_Cross_Output
{
    float3 fs_TexCoord : TEXCOORD0;
    float fs_Opacity : TEXCOORD1;
    float4 gl_Position : SV_Position;
};

void vert_main()
{
    fs_TexCoord = float3(-vs_Position.x, vs_Position.y, vs_Position.z);
    fs_Opacity = vs_Opacity;
    gl_Position = mul(float4(vs_Position, 1.0f), mul(_45_World, _39_ViewProjection));
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    vs_Position = stage_input.vs_Position;
    vs_Opacity = stage_input.vs_Opacity;
    vert_main();
    SPIRV_Cross_Output stage_output;
    stage_output.gl_Position = gl_Position;
    stage_output.fs_TexCoord = fs_TexCoord;
    stage_output.fs_Opacity = fs_Opacity;
    return stage_output;
}
