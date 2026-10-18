using System.Buffers.Binary;
using System.Numerics;

namespace MethodCache.Core.Tests.Reference;

// Reference oracle: SipHash-c-d as specified by Aumasson and Bernstein (https://www.aumasson.jp/siphash/siphash.pdf),
// written block by block for clarity and independently of the production SipHashState. Published vectors pin it:
// SipHash-2-4 from the authors' reference implementation (CC0) and SipHash-1-3 from Rust's core library tests
// (MIT or Apache-2.0). Both use key bytes 00..0f and input bytes 0..length-1, and give the 64-bit output.
internal static class SipHash
{
    internal const ulong VectorKey0 = 0x0706050403020100UL;
    internal const ulong VectorKey1 = 0x0f0e0d0c0b0a0908UL;

    internal static ulong Compute(int compressionRounds, int finalizationRounds, ulong key0, ulong key1, ReadOnlySpan<byte> data)
    {
        var v = new[] { 0x736f6d6570736575UL ^ key0, 0x646f72616e646f6dUL ^ key1, 0x6c7967656e657261UL ^ key0, 0x7465646279746573UL ^ key1 };
        var whole = data.Length - data.Length % 8;
        for (var offset = 0; offset < whole; offset += 8)
            Compress(v, compressionRounds, BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
        var last = (ulong)(byte)data.Length << 56;
        for (var i = whole; i < data.Length; i++) last |= (ulong)data[i] << (8 * (i - whole));
        Compress(v, compressionRounds, last);
        v[2] ^= 0xff;
        for (var i = 0; i < finalizationRounds; i++) Round(v);
        return v[0] ^ v[1] ^ v[2] ^ v[3];
    }

    private static void Compress(ulong[] v, int rounds, ulong block)
    {
        v[3] ^= block;
        for (var i = 0; i < rounds; i++) Round(v);
        v[0] ^= block;
    }

    private static void Round(ulong[] v)
    {
        unchecked
        {
            v[0] += v[1]; v[1] = BitOperations.RotateLeft(v[1], 13); v[1] ^= v[0]; v[0] = BitOperations.RotateLeft(v[0], 32);
            v[2] += v[3]; v[3] = BitOperations.RotateLeft(v[3], 16); v[3] ^= v[2];
            v[0] += v[3]; v[3] = BitOperations.RotateLeft(v[3], 21); v[3] ^= v[0];
            v[2] += v[1]; v[1] = BitOperations.RotateLeft(v[1], 17); v[1] ^= v[2]; v[2] = BitOperations.RotateLeft(v[2], 32);
        }
    }

    // https://github.com/veorq/SipHash/blob/32d067603b93b47828700880649198e0bfbbcffa/vectors.h (vectors_sip64)
    internal static readonly ulong[] SipHash24Vectors =
    [
        0x726fdb47dd0e0e31UL, 0x74f839c593dc67fdUL, 0x0d6c8009d9a94f5aUL, 0x85676696d7fb7e2dUL,
        0xcf2794e0277187b7UL, 0x18765564cd99a68dUL, 0xcbc9466e58fee3ceUL, 0xab0200f58b01d137UL,
        0x93f5f5799a932462UL, 0x9e0082df0ba9e4b0UL, 0x7a5dbbc594ddb9f3UL, 0xf4b32f46226bada7UL,
        0x751e8fbc860ee5fbUL, 0x14ea5627c0843d90UL, 0xf723ca908e7af2eeUL, 0xa129ca6149be45e5UL,
        0x3f2acc7f57c29bdbUL, 0x699ae9f52cbe4794UL, 0x4bc1b3f0968dd39cUL, 0xbb6dc91da77961bdUL,
        0xbed65cf21aa2ee98UL, 0xd0f2cbb02e3b67c7UL, 0x93536795e3a33e88UL, 0xa80c038ccd5ccec8UL,
        0xb8ad50c6f649af94UL, 0xbce192de8a85b8eaUL, 0x17d835b85bbb15f3UL, 0x2f2e6163076bcfadUL,
        0xde4daaaca71dc9a5UL, 0xa6a2506687956571UL, 0xad87a3535c49ef28UL, 0x32d892fad841c342UL,
        0x7127512f72f27cceUL, 0xa7f32346f95978e3UL, 0x12e0b01abb051238UL, 0x15e034d40fa197aeUL,
        0x314dffbe0815a3b4UL, 0x027990f029623981UL, 0xcadcd4e59ef40c4dUL, 0x9abfd8766a33735cUL,
        0x0e3ea96b5304a7d0UL, 0xad0c42d6fc585992UL, 0x187306c89bc215a9UL, 0xd4a60abcf3792b95UL,
        0xf935451de4f21df2UL, 0xa9538f0419755787UL, 0xdb9acddff56ca510UL, 0xd06c98cd5c0975ebUL,
        0xe612a3cb9ecba951UL, 0xc766e62cfcadaf96UL, 0xee64435a9752fe72UL, 0xa192d576b245165aUL,
        0x0a8787bf8ecb74b2UL, 0x81b3e73d20b49b6fUL, 0x7fa8220ba3b2eceaUL, 0x245731c13ca42499UL,
        0xb78dbfaf3a8d83bdUL, 0xea1ad565322a1a0bUL, 0x60e61c23a3795013UL, 0x6606d7e446282b93UL,
        0x6ca4ecb15c5f91e1UL, 0x9f626da15c9625f3UL, 0xe51b38608ef25f57UL, 0x958a324ceb064572UL,
    ];

    // https://github.com/rust-lang/rust/blob/master/library/coretests/tests/hash/sip.rs (test_siphash_1_3)
    internal static readonly ulong[] SipHash13Vectors =
    [
        0xabac0158050fc4dcUL, 0xc9f49bf37d57ca93UL, 0x82cb9b024dc7d44dUL, 0x8bf80ab8e7ddf7fbUL,
        0xcf75576088d38328UL, 0xdef9d52f49533b67UL, 0xc50d2b50c59f22a7UL, 0xd3927d989bb11140UL,
        0x369095118d299a8eUL, 0x25a48eb36c063de4UL, 0x79de85ee92ff097fUL, 0x70c118c1f94dc352UL,
        0x78a384b157b4d9a2UL, 0x306f760c1229ffa7UL, 0x605aa111c0f95d34UL, 0xd320d86d2a519956UL,
        0xcc4fdd1a7d908b66UL, 0x9cf2689063dbd80cUL, 0x8ffc389cb473e63eUL, 0xf21f9de58d297d1cUL,
        0xc0dc2f46a6cce040UL, 0xb992abfe2b45f844UL, 0x7ffe7b9ba320872eUL, 0x525a0e7fdae6c123UL,
        0xf464aeb267349c8cUL, 0x45cd5928705b0979UL, 0x3a3e35e3ca9913a5UL, 0xa91dc74e4ade3b35UL,
        0xfb0bed02ef6cd00dUL, 0x88d93cb44ab1e1f4UL, 0x540f11d643c5e663UL, 0x2370dd1f8c21d1bcUL,
        0x81157b6c16a7b60dUL, 0x4d54b9e57a8ff9bfUL, 0x759f12781f2a753eUL, 0xcea1a3bebf186b91UL,
        0x2cf508d3ada26206UL, 0xb6101c2da3c33057UL, 0xb3f47496ae3a36a1UL, 0x626b57547b108392UL,
        0xc1d2363299e41531UL, 0x667cc1923f1ad944UL, 0x65704ffec8138825UL, 0x24f280d1c28949a6UL,
        0xc2ca1cedfaf8876bUL, 0xc2164bfc9f042196UL, 0xa16e9c9368b1d623UL, 0x49fb169c8b5114fdUL,
        0x9f3143f8df074c46UL, 0xc6fdaf2412cc86b3UL, 0x7eaf49d10a52098fUL, 0x1cf313559d292f9aUL,
        0xc44a30dda2f41f12UL, 0x36fae98943a71ed0UL, 0x318fb34c73f0bce6UL, 0xa27abf3670a7e980UL,
        0xb4bcc0db243c6d75UL, 0x23f8d852fdb71513UL, 0x8f035f4da67d8a08UL, 0xd89cd0e5b7e8f148UL,
        0xf6f4e6bcf7a644eeUL, 0xaec59ad80f1837f2UL, 0xc3b2f6154b6694e0UL, 0x9d199062b7bbb3a8UL,
    ];
}
