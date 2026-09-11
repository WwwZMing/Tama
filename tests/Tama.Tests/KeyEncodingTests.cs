using System.Security.Cryptography;
using System.Text;
using Tama.Services.Crypto;

namespace Tama.Tests;

/// <summary>
/// KeyEncoding（SPKI 解析 / 密钥类型检测 / COSE 编码）行为锁定测试。
/// 背景：WebAuthn 曾因手写固定偏移解析 P-256 SPKI 踩坑（0x04 前缀 offset 26/27 之争）、
/// 因 COSE map 键非 canonical 顺序被微软 500——这些单测把正确行为固化下来防回归。
/// </summary>
public class KeyEncodingTests
{
    // === ExtractP256Raw ===

    [Fact]
    public void ExtractP256Raw_RealP256Spki_ReturnsMatchingXY()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = ecdsa.ExportSubjectPublicKeyInfo();
        var parameters = ecdsa.ExportParameters(false);

        var (x, y) = KeyEncoding.ExtractP256Raw(spki);

        Assert.Equal(32, x.Length);
        Assert.Equal(32, y.Length);
        Assert.Equal(parameters.Q.X!, x);
        Assert.Equal(parameters.Q.Y!, y);
    }

    [Fact]
    public void ExtractP256Raw_RsaSpki_Throws()
    {
        using var rsa = RSA.Create(2048);
        var spki = rsa.ExportSubjectPublicKeyInfo();

        var ex = Assert.Throws<CryptographicException>(() => KeyEncoding.ExtractP256Raw(spki));
        Assert.Contains("id-ecPublicKey", ex.Message);
    }

    [Fact]
    public void ExtractP256Raw_P384Spki_Throws()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var spki = ecdsa.ExportSubjectPublicKeyInfo();

        var ex = Assert.Throws<CryptographicException>(() => KeyEncoding.ExtractP256Raw(spki));
        Assert.Contains("prime256v1", ex.Message);
    }

    [Fact]
    public void ExtractP256Raw_Garbage_Throws()
    {
        Assert.Throws<CryptographicException>(() => KeyEncoding.ExtractP256Raw(new byte[] { 1, 2, 3 }));
        Assert.Throws<CryptographicException>(() => KeyEncoding.ExtractP256Raw(Array.Empty<byte>()));
        Assert.Throws<CryptographicException>(() => KeyEncoding.ExtractP256Raw(new byte[100]));
    }

    // === DetectKeyType / IsRsa ===

    [Fact]
    public void DetectKeyType_RsaSpki_ReturnsRsa()
    {
        using var rsa = RSA.Create(2048);
        var spki = rsa.ExportSubjectPublicKeyInfo();

        Assert.Equal(KeyEncoding.KeyType.Rsa, KeyEncoding.DetectKeyType(spki));
        Assert.True(KeyEncoding.IsRsa(spki));
    }

    [Fact]
    public void DetectKeyType_P256Spki_ReturnsEcP256()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = ecdsa.ExportSubjectPublicKeyInfo();

        Assert.Equal(KeyEncoding.KeyType.EcP256, KeyEncoding.DetectKeyType(spki));
        Assert.False(KeyEncoding.IsRsa(spki));
    }

    [Fact]
    public void DetectKeyType_Garbage_Throws()
    {
        Assert.Throws<CryptographicException>(() => KeyEncoding.DetectKeyType(new byte[] { 1, 2, 3 }));
    }

    // === COSE 编码（锁定 canonical 键序）===

    [Fact]
    public void EncodeCoseP256Key_CanonicalOrder_ExactBytes()
    {
        var x = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var y = Enumerable.Repeat((byte)0x22, 32).ToArray();

        var encoded = KeyEncoding.EncodeCoseP256Key(x, y);

        // canonical 键序：-3 < -2 < -1 < 1 < 3（微软严格解析器对乱序 500）
        // A5 | 22 5820 <y> | 21 5820 <x> | 20 01 | 01 02 | 03 26
        var expected = Hex(
            "A5" +
            "22" + "5820" + new string('2', 64) +
            "21" + "5820" + new string('1', 64) +
            "20" + "01" +
            "01" + "02" +
            "03" + "26");
        Assert.Equal(expected, encoded);
    }

    [Fact]
    public void EncodeCoseRsaKey_CanonicalOrder_ExactBytes()
    {
        var n = new byte[] { 1, 2, 3 };
        var e = new byte[] { 1, 1 };

        var encoded = KeyEncoding.EncodeCoseRsaKey(n, e);

        // canonical 键序：-2 < -1 < 1 < 3（-2 编码 0x21、-1 编码 0x20）
        // A4 | 21 42 0101 | 20 43 010203 | 01 03 | 03 390100（-257：值 256 需 2 字节 → 0x39 0x0100）
        var expected = Hex(
            "A4" +
            "21" + "42" + "0101" +
            "20" + "43" + "010203" +
            "01" + "03" +
            "03" + "390100");
        Assert.Equal(expected, encoded);
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);
}

/// <summary>KeyDerivation（共享 PBKDF2-SHA256）行为锁定测试。</summary>
public class KeyDerivationTests
{
    [Fact]
    public void Pbkdf2Sha256_Rfc7914Vector_Matches()
    {
        // RFC 7914 §11 测试向量（PBKDF2-HMAC-SHA256）：P="password", S="salt", c=1, dkLen=32
        var expected = Hex("120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b");

        var actual = KeyDerivation.Pbkdf2Sha256("password", Encoding.UTF8.GetBytes("salt"), iterations: 1);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Pbkdf2Sha256_ByteOverload_MatchesStringOverload()
    {
        var passwordBytes = Encoding.UTF8.GetBytes("correct horse battery staple");
        var salt = Encoding.UTF8.GetBytes("nacl");

        var fromString = KeyDerivation.Pbkdf2Sha256("correct horse battery staple", salt, iterations: 1000);
        var fromBytes = KeyDerivation.Pbkdf2Sha256(passwordBytes, salt, iterations: 1000);

        Assert.Equal(fromString, fromBytes);
    }

    [Fact]
    public void Pbkdf2Sha256_OutputLength_Respected()
    {
        var r = KeyDerivation.Pbkdf2Sha256("p", Encoding.UTF8.GetBytes("s"), iterations: 1, outputLength: 16);
        Assert.Equal(16, r.Length);
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);
}
