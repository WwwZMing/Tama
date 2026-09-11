using System.Formats.Asn1;
using System.Security.Cryptography;
using Tama.Services.WebAuthn;

namespace Tama.Services.Crypto;

/// <summary>
/// WebAuthn 密钥编解码工具（SPKI ↔ 裸公钥 / COSE_Key），集中处理 ASN.1 解析。
/// 历史教训：各 Service 曾手写固定偏移解析 P-256 SPKI（0x04 前缀在 offset 26 还是 27 之争，
/// 网上资料也常写错）——统一改用 System.Formats.Asn1 标准解析（零偏移魔法数），并由单测锁定行为。
/// </summary>
public static class KeyEncoding
{
    /// <summary>rsaEncryption OID（1.2.840.113549.1.1.1）</summary>
    public const string OidRsaEncryption = "1.2.840.113549.1.1.1";
    /// <summary>id-ecPublicKey OID（1.2.840.10045.2.1）</summary>
    public const string OidEcPublicKey = "1.2.840.10045.2.1";
    /// <summary>prime256v1 / secp256r1 OID（1.2.840.10045.3.1.7）</summary>
    public const string OidPrime256V1 = "1.2.840.10045.3.1.7";

    /// <summary>SPKI 密钥类型（按 AlgorithmIdentifier OID 判定）。</summary>
    public enum KeyType
    {
        /// <summary>EC P-256（id-ecPublicKey + prime256v1）—— ES256 / -7</summary>
        EcP256,
        /// <summary>RSA（rsaEncryption）—— RS256 / -257</summary>
        Rsa,
    }

    /// <summary>检测 SPKI 的密钥算法（RSA vs EC P-256）；未知 OID 或畸形结构抛 CryptographicException。</summary>
    public static KeyType DetectKeyType(byte[] spki)
    {
        try
        {
            return ReadAlgorithmOid(spki) switch
            {
                OidRsaEncryption => KeyType.Rsa,
                OidEcPublicKey => KeyType.EcP256,
                var oid => throw new CryptographicException($"Unsupported SPKI algorithm OID: {oid}"),
            };
        }
        catch (AsnContentException ex)
        {
            throw new CryptographicException("Invalid SPKI structure", ex);
        }
    }

    /// <summary>SPKI 是否为 RSA 密钥。</summary>
    public static bool IsRsa(byte[] spki) => DetectKeyType(spki) == KeyType.Rsa;

    /// <summary>
    /// 从 DER SubjectPublicKeyInfo 解析 P-256 裸公钥 (x, y)，各 32 字节。
    /// 标准 ASN.1 解析（非固定偏移）：校验 OID 为 id-ecPublicKey + prime256v1，
    /// BIT STRING 内容须为 0x04（uncompressed point）|| X(32) || Y(32)。
    /// </summary>
    public static (byte[] x, byte[] y) ExtractP256Raw(byte[] spki)
    {
        try
        {
            var reader = new AsnReader(spki, AsnEncodingRules.DER);
            var seq = reader.ReadSequence();
            var alg = seq.ReadSequence();
            var oid = alg.ReadObjectIdentifier();
            if (oid != OidEcPublicKey)
                throw new CryptographicException($"Expected id-ecPublicKey, got {oid}");
            if (alg.HasData && alg.ReadObjectIdentifier() != OidPrime256V1)
                throw new CryptographicException("Expected prime256v1 curve");
            var keyBits = seq.ReadBitString(out _);
            if (keyBits.Length != 65 || keyBits[0] != 0x04)
                throw new CryptographicException("Unexpected EC point (expected uncompressed P-256, 65 bytes)");
            return (keyBits[1..33], keyBits[33..65]);
        }
        catch (AsnContentException ex)
        {
            throw new CryptographicException("Invalid SPKI structure", ex);
        }
    }

    /// <summary>COSE_Key（EC2, ES256/-7）：{1:kty=2, 3:alg=-7, -1:crv=1, -2:x, -3:y}，键按 RFC 8949 canonical 升序。
    /// canonical 键序缺失会让微软等严格解析器 500（HAR 实证），键序必须 -3 &lt; -2 &lt; -1 &lt; 1 &lt; 3。</summary>
    public static byte[] EncodeCoseP256Key(byte[] x, byte[] y)
    {
        var cbor = new CborWriter();
        cbor.WriteMapStart(5);
        cbor.WriteInt(-3); cbor.WriteBytes(y);   // y
        cbor.WriteInt(-2); cbor.WriteBytes(x);   // x
        cbor.WriteInt(-1); cbor.WriteInt(1);     // crv: P-256
        cbor.WriteInt(1); cbor.WriteInt(2);      // kty: EC2
        cbor.WriteInt(3); cbor.WriteInt(-7);     // alg: ES256
        return cbor.ToArray();
    }

    /// <summary>COSE_Key（RSA, RS256/-257）：{1:kty=3, 3:alg=-257, -1:n, -2:e}，键按 canonical 升序。
    /// n = 模数（256 字节），e = 指数（通常 01 00 01）。</summary>
    public static byte[] EncodeCoseRsaKey(byte[] n, byte[] e)
    {
        var cbor = new CborWriter();
        cbor.WriteMapStart(4);
        cbor.WriteInt(-2); cbor.WriteBytes(e);   // e
        cbor.WriteInt(-1); cbor.WriteBytes(n);   // n
        cbor.WriteInt(1); cbor.WriteInt(3);      // kty: RSA
        cbor.WriteInt(3); cbor.WriteInt(-257);   // alg: RS256
        return cbor.ToArray();
    }

    /// <summary>读取 SPKI 顶层 AlgorithmIdentifier 的 OID。</summary>
    private static string ReadAlgorithmOid(byte[] spki)
    {
        var reader = new AsnReader(spki, AsnEncodingRules.DER);
        var seq = reader.ReadSequence();
        var alg = seq.ReadSequence();
        return alg.ReadObjectIdentifier();
    }
}
