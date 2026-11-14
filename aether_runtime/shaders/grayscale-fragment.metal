#pragma clang diagnostic ignored "-Wmissing-prototypes"
#pragma clang diagnostic ignored "-Wmissing-braces"

#include <metal_stdlib>
#include <simd/simd.h>

using namespace metal;

template<typename T, size_t Num>
struct spvUnsafeArray
{
    T elements[Num ? Num : 1];

    thread T& operator [] (size_t pos) thread
    {
        return elements[pos];
    }
    constexpr const thread T& operator [] (size_t pos) const thread
    {
        return elements[pos];
    }

    device T& operator [] (size_t pos) device
    {
        return elements[pos];
    }
    constexpr const device T& operator [] (size_t pos) const device
    {
        return elements[pos];
    }

    constexpr const constant T& operator [] (size_t pos) const constant
    {
        return elements[pos];
    }

    threadgroup T& operator [] (size_t pos) threadgroup
    {
        return elements[pos];
    }
    constexpr const threadgroup T& operator [] (size_t pos) const threadgroup
    {
        return elements[pos];
    }
};

constant spvUnsafeArray<uint, 256> _87 = spvUnsafeArray<uint, 256>({ 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 1u, 1u, 1u, 1u, 1u, 1u, 1u, 1u, 1u, 2u, 2u, 2u, 2u, 2u, 2u, 2u, 2u, 2u, 3u, 3u, 3u, 3u, 3u, 3u, 3u, 3u, 3u, 4u, 4u, 4u, 4u, 4u, 4u, 4u, 4u, 4u, 5u, 5u, 5u, 5u, 5u, 5u, 5u, 5u, 5u, 6u, 6u, 6u, 6u, 6u, 6u, 6u, 6u, 6u, 7u, 7u, 7u, 7u, 7u, 7u, 7u, 7u, 7u, 7u, 8u, 8u, 8u, 8u, 8u, 8u, 8u, 8u, 8u, 9u, 9u, 9u, 9u, 9u, 9u, 9u, 9u, 9u, 10u, 10u, 10u, 10u, 10u, 10u, 10u, 10u, 10u, 11u, 11u, 11u, 11u, 11u, 11u, 11u, 11u, 11u, 12u, 12u, 12u, 12u, 12u, 12u, 12u, 12u, 12u, 13u, 13u, 13u, 13u, 13u, 13u, 13u, 13u, 13u, 14u, 14u, 14u, 14u, 14u, 14u, 14u, 14u, 14u, 14u, 15u, 15u, 15u, 15u, 15u, 15u, 15u, 15u, 15u, 16u, 16u, 16u, 16u, 16u, 16u, 16u, 16u, 16u, 17u, 17u, 17u, 17u, 17u, 17u, 17u, 17u, 17u, 18u, 18u, 18u, 18u, 18u, 18u, 18u, 18u, 18u, 19u, 19u, 19u, 19u, 19u, 19u, 19u, 19u, 19u, 20u, 20u, 20u, 20u, 20u, 20u, 20u, 20u, 20u, 21u, 21u, 21u, 21u, 21u, 21u, 21u, 21u, 21u, 21u, 22u, 22u, 22u, 22u, 22u, 22u, 22u, 22u, 22u, 23u, 23u, 23u, 23u, 23u, 23u, 23u, 23u, 23u, 24u, 24u, 24u, 24u, 24u, 24u, 24u, 24u, 24u, 25u, 25u, 25u, 25u, 25u, 25u, 25u, 25u, 25u, 26u, 26u, 26u, 26u, 26u, 26u, 26u, 26u, 26u, 27u, 27u, 27u, 27u, 27u, 27u, 27u, 27u, 27u });
constant spvUnsafeArray<uint, 256> _217 = spvUnsafeArray<uint, 256>({ 0u, 0u, 1u, 1u, 2u, 2u, 3u, 4u, 4u, 5u, 5u, 6u, 7u, 7u, 8u, 8u, 9u, 10u, 10u, 11u, 11u, 12u, 12u, 13u, 14u, 14u, 15u, 15u, 16u, 17u, 17u, 18u, 18u, 19u, 20u, 20u, 21u, 21u, 22u, 23u, 23u, 24u, 24u, 25u, 25u, 26u, 27u, 27u, 28u, 28u, 29u, 30u, 30u, 31u, 31u, 32u, 33u, 33u, 34u, 34u, 35u, 35u, 36u, 37u, 37u, 38u, 38u, 39u, 40u, 40u, 41u, 41u, 42u, 43u, 43u, 44u, 44u, 45u, 46u, 46u, 47u, 47u, 48u, 48u, 49u, 50u, 50u, 51u, 51u, 52u, 53u, 53u, 54u, 54u, 55u, 56u, 56u, 57u, 57u, 58u, 58u, 59u, 60u, 60u, 61u, 61u, 62u, 63u, 63u, 64u, 64u, 65u, 66u, 66u, 67u, 67u, 68u, 69u, 69u, 70u, 70u, 71u, 71u, 72u, 73u, 73u, 74u, 74u, 75u, 76u, 76u, 77u, 77u, 78u, 79u, 79u, 80u, 80u, 81u, 81u, 82u, 83u, 83u, 84u, 84u, 85u, 86u, 86u, 87u, 87u, 88u, 89u, 89u, 90u, 90u, 91u, 92u, 92u, 93u, 93u, 94u, 94u, 95u, 96u, 96u, 97u, 97u, 98u, 99u, 99u, 100u, 100u, 101u, 102u, 102u, 103u, 103u, 104u, 104u, 105u, 106u, 106u, 107u, 107u, 108u, 109u, 109u, 110u, 110u, 111u, 112u, 112u, 113u, 113u, 114u, 115u, 115u, 116u, 116u, 117u, 117u, 118u, 119u, 119u, 120u, 120u, 121u, 122u, 122u, 123u, 123u, 124u, 125u, 125u, 126u, 126u, 127u, 127u, 128u, 129u, 129u, 130u, 130u, 131u, 132u, 132u, 133u, 133u, 134u, 135u, 135u, 136u, 136u, 137u, 138u, 138u, 139u, 139u, 140u, 140u, 141u, 142u, 142u, 143u, 143u, 144u, 145u, 145u, 146u, 146u, 147u, 148u, 148u, 149u, 149u, 150u });
constant spvUnsafeArray<uint, 256> _224 = spvUnsafeArray<uint, 256>({ 0u, 0u, 0u, 0u, 1u, 1u, 1u, 2u, 2u, 2u, 3u, 3u, 3u, 3u, 4u, 4u, 4u, 5u, 5u, 5u, 6u, 6u, 6u, 6u, 7u, 7u, 7u, 8u, 8u, 8u, 9u, 9u, 9u, 9u, 10u, 10u, 10u, 11u, 11u, 11u, 12u, 12u, 12u, 12u, 13u, 13u, 13u, 14u, 14u, 14u, 15u, 15u, 15u, 15u, 16u, 16u, 16u, 17u, 17u, 17u, 18u, 18u, 18u, 18u, 19u, 19u, 19u, 20u, 20u, 20u, 21u, 21u, 21u, 21u, 22u, 22u, 22u, 23u, 23u, 23u, 24u, 24u, 24u, 24u, 25u, 25u, 25u, 26u, 26u, 26u, 27u, 27u, 27u, 27u, 28u, 28u, 28u, 29u, 29u, 29u, 30u, 30u, 30u, 30u, 31u, 31u, 31u, 32u, 32u, 32u, 33u, 33u, 33u, 33u, 34u, 34u, 34u, 35u, 35u, 35u, 36u, 36u, 36u, 36u, 37u, 37u, 37u, 38u, 38u, 38u, 39u, 39u, 39u, 40u, 40u, 40u, 40u, 41u, 41u, 41u, 42u, 42u, 42u, 43u, 43u, 43u, 43u, 44u, 44u, 44u, 45u, 45u, 45u, 46u, 46u, 46u, 46u, 47u, 47u, 47u, 48u, 48u, 48u, 49u, 49u, 49u, 49u, 50u, 50u, 50u, 51u, 51u, 51u, 52u, 52u, 52u, 52u, 53u, 53u, 53u, 54u, 54u, 54u, 55u, 55u, 55u, 55u, 56u, 56u, 56u, 57u, 57u, 57u, 58u, 58u, 58u, 58u, 59u, 59u, 59u, 60u, 60u, 60u, 61u, 61u, 61u, 61u, 62u, 62u, 62u, 63u, 63u, 63u, 64u, 64u, 64u, 64u, 65u, 65u, 65u, 66u, 66u, 66u, 67u, 67u, 67u, 67u, 68u, 68u, 68u, 69u, 69u, 69u, 70u, 70u, 70u, 70u, 71u, 71u, 71u, 72u, 72u, 72u, 73u, 73u, 73u, 73u, 74u, 74u, 74u, 75u, 75u, 75u, 76u, 76u, 76u });

struct main0_out
{
    float4 OutColor [[color(0)]];
};

struct main0_in
{
    float2 fs_TexCoord [[user(locn0)]];
};

static inline __attribute__((always_inline))
uint quantize(thread const float& f)
{
    return uint((f * 255.0) + 0.5);
}

fragment main0_out main0(main0_in in [[stage_in]], texture2d<float> Input [[texture(0)]], sampler Sampler [[sampler(0)]])
{
    main0_out out = {};
    float4 texel = Input.sample(Sampler, in.fs_TexCoord);
    float param = texel.x;
    float param_1 = texel.y;
    float param_2 = texel.z;
    uint3 t = uint3(quantize(param), quantize(param_1), quantize(param_2));
    uint grayscale = (_87[t.x] + _217[t.y]) + _224[t.z];
    grayscale %= 255u;
    out.OutColor = float4(float3(float(grayscale)) / float3(255.0), texel.w);
    return out;
}
