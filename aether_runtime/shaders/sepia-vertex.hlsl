static const float4 _21[3] = { float4(-1.0f, -1.0f, 0.0f, 1.0f), float4(-1.0f, 3.0f, 0.0f, -1.0f), float4(3.0f, -1.0f, 2.0f, 1.0f) };

static float4 gl_Position;
static int gl_VertexIndex;
static float2 fs_TexCoord;

struct SPIRV_Cross_Input
{
    uint gl_VertexIndex : SV_VertexID;
};

struct SPIRV_Cross_Output
{
    float2 fs_TexCoord : TEXCOORD0;
    float4 gl_Position : SV_Position;
};

void vert_main()
{
    float4 vertex = _21[gl_VertexIndex];
    gl_Position = float4(vertex.xy, 0.0f, 1.0f);
    fs_TexCoord = vertex.zw;
}

SPIRV_Cross_Output main(SPIRV_Cross_Input stage_input)
{
    gl_VertexIndex = int(stage_input.gl_VertexIndex);
    vert_main();
    SPIRV_Cross_Output stage_output;
    stage_output.gl_Position = gl_Position;
    stage_output.fs_TexCoord = fs_TexCoord;
    return stage_output;
}
