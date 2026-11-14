#version 450

#extension GL_EXT_samplerless_texture_functions : enable

layout(set = 1, binding = 0) uniform texture2D Texture;
layout(set = 1, binding = 1) uniform texture2D Mask;
layout(set = 1, binding = 2) uniform sampler Sampler;

layout(set = 2, binding = 0) uniform TransitionParameters
{
    vec4 _Amounts; // progress, feather, render scale, reserved
    vec4 _Viewport; // framebuffer viewport origin, reserved
    vec4 _Coverage[64]; // packed legacy cumulative coverage for byte values 0..255
};

layout(location = 0) in vec4 fs_Color;
layout(location = 1) in vec2 fs_TexCoord;
layout(location = 0) out vec4 OutColor;

void main()
{
    vec2 maskUV = (gl_FragCoord.xy - _Viewport.xy) / _Amounts.z / textureSize(Mask, 0);
    float mask = texture(sampler2D(Mask, Sampler), maskUV).r;
    float feather = _Amounts.y;
    float threshold = mask * (1.0 - feather) + feather * 0.5;
    float coverage = feather > 0.0
        ? clamp((_Amounts.x - threshold) / feather + 0.5, 0.0, 1.0)
        : step(threshold, _Amounts.x);
    if (_Amounts.x <= 0.0) coverage = 0.0;
    if (_Amounts.x >= 1.0) coverage = 1.0;
    if (_Amounts.w > 0.0)
    {
        int index = _Amounts.w >= 2.0 ? int(round(clamp(mask, 0.0, 1.0) * 255.0)) : 0;
        coverage = _Coverage[index / 4][index % 4];
    }
    vec4 texel = texture(sampler2D(Texture, Sampler), fs_TexCoord);
    float alpha = texel.a * fs_Color.a * coverage;
    OutColor = vec4((texel * fs_Color * alpha).xyz, alpha);
}
