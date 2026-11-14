#pragma clang diagnostic ignored "-Wmissing-prototypes"

#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

// Implementation of signed integer mod accurate to SPIR-V specification
template<typename Tx, typename Ty>
inline Tx spvSMod(Tx x, Ty y)
{
    Tx remainder = x - y * (x / y);
    return select(Tx(remainder + y), remainder, remainder == 0 || (x >= 0) == (y >= 0));
}

struct TransitionParameters
{
    float4 _Amounts;
    float4 _Viewport;
    float4 _Coverage[64];
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

fragment main0_out main0(main0_in in [[stage_in]], constant TransitionParameters& _20 [[buffer(1)]], texture2d<float> Mask [[texture(1)]], texture2d<float> Texture [[texture(0)]], sampler Sampler [[sampler(0)]], float4 gl_FragCoord [[position]])
{
    main0_out out = {};
    float2 maskUV = ((gl_FragCoord.xy - _20._Viewport.xy) / float2(_20._Amounts.z)) / float2(int2(Mask.get_width(), Mask.get_height()));
    float mask = Mask.sample(Sampler, maskUV).x;
    float feather = _20._Amounts.y;
    float threshold = (mask * (1.0 - feather)) + (feather * 0.5);
    float _75;
    if (feather > 0.0)
    {
        _75 = fast::clamp(((_20._Amounts.x - threshold) / feather) + 0.5, 0.0, 1.0);
    }
    else
    {
        _75 = step(threshold, _20._Amounts.x);
    }
    float coverage = _75;
    if (_20._Amounts.x <= 0.0)
    {
        coverage = 0.0;
    }
    if (_20._Amounts.x >= 1.0)
    {
        coverage = 1.0;
    }
    if (_20._Amounts.w > 0.0)
    {
        int _114;
        if (_20._Amounts.w >= 2.0)
        {
            _114 = int(round(fast::clamp(mask, 0.0, 1.0) * 255.0));
        }
        else
        {
            _114 = 0;
        }
        int index = _114;
        coverage = _20._Coverage[index / 4][spvSMod(index, 4)];
    }
    float4 texel = Texture.sample(Sampler, in.fs_TexCoord);
    float alpha = (texel.w * in.fs_Color.w) * coverage;
    out.OutColor = float4(((texel * in.fs_Color) * alpha).xyz, alpha);
    return out;
}
