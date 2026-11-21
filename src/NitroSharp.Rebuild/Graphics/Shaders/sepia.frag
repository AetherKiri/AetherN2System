#version 450

layout(set = 0, binding = 0) uniform texture2D Input;
layout(set = 0, binding = 1) uniform sampler Sampler;
layout(location = 0) in vec2 fs_TexCoord;
layout(location = 0) out vec4 OutColor;

void main()
{
    vec4 texel = texture(sampler2D(Input, Sampler), fs_TexCoord);
    uvec3 rgb = uvec3(texel.rgb * 255.0 + 0.5);
    uvec3 terms = (rgb * uvec3(28, 151, 77)) >> 8;
    uint gray = terms.r + terms.g + terms.b;
    uint warm = min(gray + 30u, 255u);
    OutColor = vec4(vec3(warm, warm, gray) / 255.0, texel.a);
}
