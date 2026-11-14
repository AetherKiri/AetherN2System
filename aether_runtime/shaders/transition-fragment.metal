#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct TransitionParameters
{
    float4 _Amounts;
    float4 _Viewport;
};

struct main0_out
{
    float4 OutColor [[color(0)]];
};

struct main0_in
{
    float4 fs_Color [[user(locn0)]];
    float2 fs_TexCoord [[user(locn1)]];
};

fragment main0_out main0(main0_in in [[stage_in]], constant TransitionParameters& _17 [[buffer(1)]], texture2d<float> Mask [[texture(1)]], texture2d<float> Texture [[texture(0)]], sampler Sampler [[sampler(0)]], float4 gl_FragCoord [[position]])
{
    main0_out out = {};
    float2 maskUV = ((gl_FragCoord.xy - _17._Viewport.xy) / float2(_17._Amounts.z)) / float2(int2(Mask.get_width(), Mask.get_height()));
    float mask = Mask.sample(Sampler, maskUV).x;
    float feather = _17._Amounts.y;
    float threshold = (mask * (1.0 - feather)) + (feather * 0.5);
    float _73;
    if (feather > 0.0)
    {
        _73 = fast::clamp(((_17._Amounts.x - threshold) / feather) + 0.5, 0.0, 1.0);
    }
    else
    {
        _73 = step(threshold, _17._Amounts.x);
    }
    float coverage = _73;
    if (_17._Amounts.x <= 0.0)
    {
        coverage = 0.0;
    }
    if (_17._Amounts.x >= 1.0)
    {
        coverage = 1.0;
    }
    float4 texel = Texture.sample(Sampler, in.fs_TexCoord);
    float alpha = (texel.w * in.fs_Color.w) * coverage;
    out.OutColor = float4(((texel * in.fs_Color) * alpha).xyz, alpha);
    return out;
}
