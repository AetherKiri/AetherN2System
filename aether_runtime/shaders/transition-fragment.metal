#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

struct FadeAmount
{
    float _FadeAmount;
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

fragment main0_out main0(main0_in in [[stage_in]], constant FadeAmount& _35 [[buffer(1)]], texture2d<float> Mask [[texture(1)]], texture2d<float> Texture [[texture(0)]], sampler Sampler [[sampler(0)]])
{
    main0_out out = {};
    float mask = (Mask.sample(Sampler, in.fs_TexCoord).x * 0.89999997615814208984375) + 0.0500000007450580596923828125;
    float alpha = fast::clamp(((_35._FadeAmount - mask) / 0.100000001490116119384765625) + 0.5, 0.0, 1.0);
    out.OutColor = (Texture.sample(Sampler, in.fs_TexCoord) * in.fs_Color) * alpha;
    return out;
}
