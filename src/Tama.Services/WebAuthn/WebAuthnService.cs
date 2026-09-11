using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tama.Core.Contracts;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Crypto;
using Tama.Services.Passkey;
using Microsoft.EntityFrameworkCore;
using Serilog;

using Tama.Core.Exceptions;

namespace Tama.Services.WebAuthn;

/// <summary>
/// 真实 WebAuthn 协议层（RFC 8809 / W3C WebAuthn Level 2 的认证器侧实现）。
/// 与 PasskeyService（Tama 自闭环、简化签名）不同，这里产出**标准 WebAuthn 结构**：
///   create → attestationObject（fmt:none）+ clientDataJSON，可直接被标准 Relying Party 验证
///   get    → authenticatorData + clientDataJSON + signature（ECDSA-P256 DER，ES256/-7）
/// 私钥仍由 Tama 管理（主密码派生密钥 AES-GCM 加密落库），平台验证弹 Windows Hello。
/// </summary>
public class WebAuthnService : IWebAuthnApi
{
    private readonly TamaDbContext _dbVault;
    private readonly DatabaseKeyService _dbKeyService;
    private readonly IPasskeyPlatformService _platform;
    private readonly PasskeyService _passkey;
    private static readonly ILogger Log = Serilog.Log.ForContext<WebAuthnService>();

    public WebAuthnService(
        TamaDbContext dbVault,
        DatabaseKeyService dbKeyService,
        IPasskeyPlatformService platform,
        PasskeyService passkey)
    {
        _dbVault = dbVault;
        _dbKeyService = dbKeyService;
        _platform = platform;
        _passkey = passkey;
    }

    /// <summary>
    /// WebAuthn 注册：生成/复用 P-256 密钥对，产出标准 attestation 响应。
    /// </summary>
    public async Task<WebAuthnCreateResponse> Create(WebAuthnCreateRequest? req)
    {
        if (req == null || string.IsNullOrEmpty(req.RpId) || string.IsNullOrEmpty(req.Challenge) || string.IsNullOrEmpty(req.Origin))
            throw new TamaException("rpId, challenge and origin required");

        // 平台验证（Windows Hello 弹窗）
        var verified = await _platform.AuthenticateAsync("Tama", $"为 {req.RpId} 创建通行密钥");
        if (!verified)
            return new WebAuthnCreateResponse(Success: false, Reason: "user-cancelled",
                Id: null, RawId: null, Type: null, PublicKey: null, PublicKeyAlgorithm: null,
                Transports: null, Response: null, AuthenticatorAttachment: null, RpId: null, UserName: null);

        var userName = req.UserName ?? "user";
        // 真实网站（微软 MSA 等）的通行密钥用 RSA-2048/RS256：原生 Windows Hello 的 MSA 断言
        // 签名就是 256 字节 RSA-2048 PKCS#1 v1.5；ES256 注册能被接受但断言会被拒（"Fido assertion is invalid"）。
        var record = await _passkey.CreateOrGetCredentialAsync(req.RpId, userName, req.UserHandle, keyType: "rsa");
        Log.Information("WebAuthn create: rpId={RpId} user={User} credential={CredId} keyType={KeyType}", req.RpId, userName, record.CredentialId, PasskeyService.IsRsaKey(record) ? "rsa" : "ec");

        var spki = Convert.FromBase64String(record.PublicKey);
        byte[] coseKey;
        if (PasskeyService.IsRsaKey(record))
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(spki, out _);
            var p = rsa.ExportParameters(false);
            coseKey = KeyEncoding.EncodeCoseRsaKey(p.Modulus!, p.Exponent!);
        }
        else
        {
            var pubRaw = KeyEncoding.ExtractP256Raw(spki);
            coseKey = KeyEncoding.EncodeCoseP256Key(pubRaw.x, pubRaw.y);
        }

        // authenticatorData（注册）：rpIdHash || flags(UP|UV|AT) || signCount || AAGUID || credIdLen || credId || COSE pubkey
        var rpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(req.RpId));
        var credId = PasskeyService.DecodeBase64Url(record.CredentialId);
        var authData = new byte[37 + 16 + 2 + credId.Length + coseKey.Length];
        rpIdHash.CopyTo(authData, 0);
        authData[32] = 0x45; // UP(1) | UV(4) | AT(0x40)
        // signCount 0..4（新凭据为 0）
        credId.CopyTo(authData, 55);
        authData[53] = (byte)(credId.Length >> 8);
        authData[54] = (byte)credId.Length;
        coseKey.CopyTo(authData, 55 + credId.Length);

        // attestationObject：{ fmt: "none", attStmt: {}, authData: bytes }
        // CBOR canonical 要求 map 键按字典序：attStmt < authData < fmt（微软等严格解析器会拒绝对乱序 500）
        var cbor = new CborWriter();
        cbor.WriteMapStart(3);
        cbor.WriteText("attStmt"); cbor.WriteMapStart(0);
        cbor.WriteText("authData"); cbor.WriteBytes(authData);
        cbor.WriteText("fmt"); cbor.WriteText("none");
        var attestationObject = cbor.ToArray();

        var clientDataJson = BuildClientDataJson("webauthn.create", req.Challenge, req.Origin);

        return new WebAuthnCreateResponse(
            Success: null,
            Reason: null,
            Id: record.CredentialId,
            RawId: record.CredentialId,
            Type: "public-key",
            // 页面 JS 会调 response.getPublicKey()/getPublicKeyAlgorithm()/getAuthenticatorData()，必须给标准字段
            PublicKey: record.PublicKey,          // DER SubjectPublicKeyInfo（base64）
            PublicKeyAlgorithm: PasskeyService.IsRsaKey(record) ? -257 : -7, // RS256 / ES256
            Transports: new List<string> { "internal" },
            Response: new WebAuthnAttestationData(
                ClientDataJSON: Convert.ToBase64String(clientDataJson),
                AttestationObject: Convert.ToBase64String(attestationObject),
                AuthenticatorData: Convert.ToBase64String(authData)),
            AuthenticatorAttachment: "platform",
            RpId: req.RpId,
            UserName: record.UserName);
    }

    /// <summary>
    /// WebAuthn 认证：定位凭据 → 弹 Windows Hello → 签名 clientDataHash || authenticatorData。
    /// </summary>
    public async Task<WebAuthnGetResponse> Get(WebAuthnGetRequest? req)
    {
        if (req == null || string.IsNullOrEmpty(req.RpId) || string.IsNullOrEmpty(req.Challenge) || string.IsNullOrEmpty(req.Origin))
            throw new TamaException("rpId, challenge and origin required");

        PasskeyCredentialRecord? record;
        if (!string.IsNullOrEmpty(req.CredentialId))
        {
            record = await _dbVault.PasskeyCredentials
                .FirstOrDefaultAsync(p => p.CredentialId == req.CredentialId);
        }
        else if (req.AllowCredentialIds is { Count: > 0 })
        {
            // RP 的 allowCredentials 里有多个凭据时，按列表匹配 Tama 库（不再"取最新"，
            // 否则返回的凭据不在 allowCredentials 里会被 RP 当未知凭据拒绝）
            record = await _dbVault.PasskeyCredentials
                .Where(p => p.RpId == req.RpId && req.AllowCredentialIds.Contains(p.CredentialId))
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();
        }
        else
        {
            record = await _dbVault.PasskeyCredentials
                .Where(p => p.RpId == req.RpId)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();
        }
        if (record == null)
            return new WebAuthnGetResponse(Success: false, Reason: "no-credential",
                Id: null, RawId: null, Type: null, Response: null, RpId: null, Counter: 0);

        Log.Information("WebAuthn get: rpId={RpId} credential={CredId} allowCredentials={Allow}",
            req.RpId, record.CredentialId, req.AllowCredentialIds == null ? null : string.Join(",", req.AllowCredentialIds));

        // 平台验证（Windows Hello 弹窗）
        var verified = await _platform.AuthenticateAsync("Tama", $"用通行密钥登录 {req.RpId}");
        if (!verified)
            return new WebAuthnGetResponse(Success: false, Reason: "user-cancelled",
                Id: null, RawId: null, Type: null, Response: null, RpId: null, Counter: 0);

        // authenticatorData（认证）：rpIdHash || flags(UP|UV) || signCount
        var rpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(req.RpId));
        // counter 只在 >0 时自增：同步型认证器（Bitwarden 的软件认证器、iCloud 钥匙串等）把 counter
        // 恒定为 0，0→1 反而像"这枚凭据被复制过"（RFC 8809 允许 0，但要求实现不要凭空造出不单调的值）。
        // Tama 自己生成的凭据从 1 起，行为不受影响。
        if (record.Counter > 0) record.Counter++;
        var authData = new byte[37];
        rpIdHash.CopyTo(authData, 0);
        authData[32] = 0x05; // UP(1) | UV(4)，无 AT
        authData[33] = (byte)(record.Counter >> 24);
        authData[34] = (byte)(record.Counter >> 16);
        authData[35] = (byte)(record.Counter >> 8);
        authData[36] = (byte)record.Counter;
        await _dbVault.SaveChangesAsync();

        // signature = sign(authenticatorData || clientDataHash)
        // WebAuthn 规范 §6.1.4：签名覆盖 authenticatorData 在前、clientDataHash 在后的拼接。
        // 早期实现写反成 clientDataHash || authData——签名密码学有效但字节顺序错，自检因
        // 用相同错误顺序验签而"通过"，微软按规范顺序验签必拒（"The Fido assertion is invalid"）。
        var clientDataJson = BuildClientDataJson("webauthn.get", req.Challenge, req.Origin);
        var clientDataHash = SHA256.HashData(clientDataJson);
        var signedData = authData.Concat(clientDataHash).ToArray();

        byte[] signature;
        if (PasskeyService.IsRsaKey(record))
        {
            // RSA-2048 / RS256(-257)：WebAuthn 的 RSA 签名 = PKCS#1 v1.5 + SHA-256，输出 256 字节原始签名（无 DER 包装）。
            // 微软 MSA 通行密钥要求 RSA——原生 Windows Hello 断言签名正是 256 字节 RSA-2048。
            using var rsa = _passkey.LoadRsaPrivateKey(record);
            signature = rsa.SignData(signedData, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        else
        {
            // ECDSA P-256 / ES256(-7)：WebAuthn 要求 signature 为 ASN.1 DER（X9.62）；.NET 11 的 SignData
            // 默认输出 raw（IEEE P1363 r||s 64 字节），不指定格式会返回 raw → 服务器按 DER 解析失败
            using var ecdsa = _passkey.LoadPrivateKey(record);
            signature = ecdsa.SignData(signedData, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }

        // 诊断：完整 assertion 摘要（对照 clientDataJSON 里的 challenge 与服务器下发的 challenge 是否一致）
        using (var cd = JsonDocument.Parse(clientDataJson))
        {
            var cdChallenge = cd.RootElement.GetProperty("challenge").GetString();
            Log.Information("WebAuthn get assertion: challengeReceived={Challenge} challengeInClientData={Cd} counter={Counter} sigLen={SigLen} userHandle={Uh} publicKey={Pk} authData={Ad} signature={Sig} clientDataJSON={Cdj}",
                req.Challenge,
                cdChallenge,
                record.Counter,
                signature.Length,
                record.UserHandle,
                record.PublicKey,
                Convert.ToBase64String(authData),
                Convert.ToBase64String(signature),
                Convert.ToBase64String(clientDataJson));
        }

        var userHandle = record.UserHandle != null ? PasskeyService.DecodeBase64Url(record.UserHandle) : Array.Empty<byte>();

        return new WebAuthnGetResponse(
            Success: null,
            Reason: null,
            Id: record.CredentialId,
            RawId: record.CredentialId,
            Type: "public-key",
            Response: new WebAuthnAssertionData(
                ClientDataJSON: Convert.ToBase64String(clientDataJson),
                AuthenticatorData: Convert.ToBase64String(authData),
                Signature: Convert.ToBase64String(signature),
                UserHandle: Convert.ToBase64String(userHandle)),
            RpId: req.RpId,
            Counter: record.Counter);
    }

    /// <summary>
    /// 探测 Tama 是否有该 rpId 的匹配凭据（供扩展决定是否拦截 navigator.credentials.get；
    /// 无匹配时必须放行原生认证器，否则会劫持 Windows Hello 的登录流程）。
    /// </summary>
    public async Task<WebAuthnProbeResponse> Probe(WebAuthnProbeRequest? req)
    {
        if (req == null || string.IsNullOrEmpty(req.RpId))
            return new WebAuthnProbeResponse(false);

        var query = _dbVault.PasskeyCredentials.Where(p => p.RpId == req.RpId);
        if (req.AllowCredentialIds is { Count: > 0 })
        {
            query = query.Where(p => req.AllowCredentialIds.Contains(p.CredentialId));
        }
        var hasMatch = await query.AnyAsync();
        Log.Information("WebAuthn probe: rpId={RpId} allow={Allow} hasMatch={HasMatch}",
            req.RpId,
            req.AllowCredentialIds == null ? null : string.Join(",", req.AllowCredentialIds),
            hasMatch);
        return new WebAuthnProbeResponse(hasMatch);
    }

    // === 编码辅助 ===

    private static byte[] BuildClientDataJson(string type, string challengeB64Url, string origin)
    {
        var challenge = PasskeyService.DecodeBase64Url(challengeB64Url);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            type,
            challenge = PasskeyService.Base64Url(challenge),
            origin,
            crossOrigin = false,
        });
    }
}
