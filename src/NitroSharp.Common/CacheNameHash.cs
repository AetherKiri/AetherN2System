using System;
using System.Buffers.Binary;
using System.Numerics;

namespace NitroSharp.Common;

// Cache filenames must stay stable across platforms. Android's system libssl
// is BoringSSL, so loading the desktop OpenSSL shim just to hash a name aborts
// NativeAOT. Compute this small, non-secret SHA-256 input entirely in managed code.
public static class CacheNameHash
{
    private static ReadOnlySpan<uint> Constants =>
    [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    ];

    public static byte[] HashData(ReadOnlySpan<byte> input)
    {
        int length = checked((input.Length + 72) / 64 * 64);
        byte[] padded = new byte[length];
        input.CopyTo(padded);
        padded[input.Length] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(length - 8), (ulong)input.Length * 8);
        Span<uint> state = stackalloc uint[]
        {
            0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
            0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
        };
        Span<uint> words = stackalloc uint[64];
        for (int offset = 0; offset < padded.Length; offset += 64)
        {
            ReadOnlySpan<byte> block = padded.AsSpan(offset, 64);
            for (int i = 0; i < 16; i++)
                words[i] = BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);
            for (int i = 16; i < 64; i++)
            {
                uint x = words[i - 15], y = words[i - 2];
                uint s0 = BitOperations.RotateRight(x, 7) ^ BitOperations.RotateRight(x, 18) ^ (x >> 3);
                uint s1 = BitOperations.RotateRight(y, 17) ^ BitOperations.RotateRight(y, 19) ^ (y >> 10);
                words[i] = unchecked(words[i - 16] + s0 + words[i - 7] + s1);
            }
            uint a = state[0], b = state[1], c = state[2], d = state[3];
            uint e = state[4], f = state[5], g = state[6], h = state[7];
            for (int i = 0; i < 64; i++)
            {
                uint s1 = BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25);
                uint choice = (e & f) ^ (~e & g);
                uint t1 = unchecked(h + s1 + choice + Constants[i] + words[i]);
                uint s0 = BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22);
                uint majority = (a & b) ^ (a & c) ^ (b & c);
                uint t2 = unchecked(s0 + majority);
                h = g; g = f; f = e; e = unchecked(d + t1);
                d = c; c = b; b = a; a = unchecked(t1 + t2);
            }
            state[0] = unchecked(state[0] + a); state[1] = unchecked(state[1] + b);
            state[2] = unchecked(state[2] + c); state[3] = unchecked(state[3] + d);
            state[4] = unchecked(state[4] + e); state[5] = unchecked(state[5] + f);
            state[6] = unchecked(state[6] + g); state[7] = unchecked(state[7] + h);
        }
        byte[] hash = new byte[32];
        for (int i = 0; i < state.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(hash.AsSpan(i * 4), state[i]);
        return hash;
    }
}
