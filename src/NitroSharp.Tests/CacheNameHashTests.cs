using System;
using System.Security.Cryptography;
using System.Text;
using NitroSharp.Common;
using Xunit;

namespace NitroSharp.Tests;

public class CacheNameHashTests
{
    [Theory]
    [InlineData("", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("abc", "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "248D6A61D20638B8E5C026930C3E6039A33CE45964FF2167F6ECEDD419DB06C1")]
    public void MatchesKnownSha256Vectors(string text, string expected)
        => Assert.Equal(expected, Convert.ToHexString(CacheNameHash.HashData(Encoding.UTF8.GetBytes(text))));

    [Fact]
    public void PreservesCacheNamesAcrossPaddingBoundaries()
    {
        var random = new Random(42);
        foreach (int length in new[] { 1, 55, 56, 63, 64, 65, 119, 120, 127, 128, 1024 })
        {
            byte[] bytes = new byte[length];
            random.NextBytes(bytes);
            Assert.Equal(SHA256.HashData(bytes), CacheNameHash.HashData(bytes));
        }
    }
}
