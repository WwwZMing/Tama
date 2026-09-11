using System.Text.Json;
using Tama.Core.Interfaces;
using Tama.Services.Bitwarden;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// 同步下来的通行密钥**怎么摆、怎么加密**——这一组是冲着 2026-09-13 查出来的两个真 bug 去的，
/// 两个都让"Bitwarden 的通行密钥"从来没进过 Tama：
///
/// 1. **位置错了**：<c>fido2Credentials</c> 挂在 <c>login</c> 里，不在 cipher 上
///    （服务端 <c>CipherLoginModel.Fido2Credentials</c>、客户端 <c>cipher.request.ts</c>、
///    以及真实导出文件三处一致）。原来写在 <c>BitwardenRawCipher</c> 上 → 那一层根本没有这个键
///    → 永远绑不到 → 主页面「通行密钥」筛选恒为 0、通行密钥页看不到云端那几枚。
/// 2. **类型错了**：除 <c>creationDate</c> 外**每个字段都是 EncString**，
///    包括 counter（"0" 的密文）与 discoverable（"true" 的密文）。原来声明成 <c>int</c>/<c>bool</c>
///    → 一旦真的绑上就会反序列化失败（"2.xxx|yyy" 转不成 Int32），**整个同步直接挂掉**。
///
/// 所以这里的 payload 是照服务端的形状手写的（不是拿我们自己上行的那份转一圈回来）：
/// 只有这样才验得出"我们读得懂服务器发的东西"。
/// </summary>
public class BitwardenFido2WireTests
{
    private static readonly byte[] EncKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] MacKey = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();

    private static string Enc(string plaintext) => BitwardenCrypto.EncryptString(plaintext, EncKey, MacKey);

    /// <summary>照服务端 sync 响应的形状写一份 payload（fido2 在 login 里，值全是 EncString）。</summary>
    private static string ServerPayload(string? credentialId = null, string? keyValue = null,
        string? rpId = null, string? counter = null, string? discoverable = null)
    {
        var payload = new
        {
            id = Guid.NewGuid().ToString(),
            type = 1,
            name = Enc("Google"),
            revisionDate = "2026-05-05T00:00:00Z",
            login = new
            {
                username = Enc("me@gmail.com"),
                fido2Credentials = new[]
                {
                    new
                    {
                        credentialId = credentialId ?? Enc("eeb8ee82-ba9a-47e5-b74b-d2f768209988"),
                        keyType = Enc("public-key"),
                        keyAlgorithm = Enc("ECDSA"),
                        keyCurve = Enc("P-256"),
                        keyValue = keyValue ?? Enc("MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg"),
                        rpId = rpId ?? Enc("google.com"),
                        rpName = Enc("Google"),
                        userHandle = Enc("R09PR0xFX0FDQ09VTlQ6MTAwMA"),
                        userName = Enc("me@gmail.com"),
                        userDisplayName = Enc("Me"),
                        counter = counter ?? Enc("7"),
                        discoverable = discoverable ?? Enc("true"),
                        creationDate = "2026-03-02T06:36:14.379Z",
                    },
                },
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static BitwardenCipherResponse Decode(string json)
    {
        var raw = JsonSerializer.Deserialize<BitwardenRawCipher>(json)!;
        return BitwardenApiClient.DecryptCipher(raw, EncKey, MacKey, new());
    }

    [Fact]
    public void Passkey_Nested_In_Login_Is_Decoded()
    {
        var c = Decode(ServerPayload());

        var f = Assert.Single(c.Fido2Credentials!);
        Assert.Equal("eeb8ee82-ba9a-47e5-b74b-d2f768209988", f.CredentialId);
        Assert.Equal("public-key", f.KeyType);
        Assert.Equal("ECDSA", f.KeyAlgorithm);
        Assert.Equal("P-256", f.KeyCurve);
        Assert.Equal("MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg", f.KeyValue);
        Assert.Equal("google.com", f.RpId);
        Assert.Equal("Google", f.RpName);
        Assert.Equal("me@gmail.com", f.UserName);
        Assert.Equal("R09PR0xFX0FDQ09VTlQ6MTAwMA", f.UserHandle);
        Assert.Equal("Me", f.UserDisplayName);
        Assert.Equal(7, f.Counter);
        Assert.True(f.Discoverable);
        Assert.Equal(new DateTime(2026, 3, 2, 6, 36, 14, 379, DateTimeKind.Utc), f.CreationDate);
    }

    [Fact]
    public void Discoverable_False_Is_Parsed_From_The_Encrypted_String()
    {
        var c = Decode(ServerPayload(discoverable: Enc("false")));

        Assert.False(Assert.Single(c.Fido2Credentials!).Discoverable);
    }

    /// <summary>counter 缺失或不是数字时不许炸（旧数据 / 自建服务器），退化成 0。</summary>
    [Fact]
    public void Unparsable_Counter_Falls_Back_To_Zero()
    {
        var c = Decode(ServerPayload(counter: Enc("not-a-number")));

        Assert.Equal(0, Assert.Single(c.Fido2Credentials!).Counter);
    }

    /// <summary>
    /// 自建服务器 / Vaultwarden / 历史数据里可能是明文（前缀不是 "2."）：按明文用，别当"解不开"丢掉。
    /// </summary>
    [Fact]
    public void Plaintext_Values_Are_Tolerated()
    {
        var c = Decode(ServerPayload(
            credentialId: "plain-cred-id", keyValue: "plain-key", rpId: "plain.example",
            counter: "3", discoverable: "true"));

        var f = Assert.Single(c.Fido2Credentials!);
        Assert.Equal("plain-cred-id", f.CredentialId);
        Assert.Equal("plain-key", f.KeyValue);
        Assert.Equal("plain.example", f.RpId);
        Assert.Equal(3, f.Counter);
        Assert.True(f.Discoverable);
    }

    /// <summary>
    /// 一枚坏凭据（完整性校验过不去）不许把整条 cipher 带走：解不开的那枚丢掉、其余的照常留下。
    /// 为什么要单独兜住：DecryptCipher 抛错会被调用方记成"解密失败"，而失败的那条在孤儿清理
    /// 看来就是"服务器上不存在" → 本地那一行会被删掉。
    /// </summary>
    [Fact]
    public void One_Broken_Passkey_Does_Not_Kill_The_Cipher()
    {
        // 拿正确的密文，把 HMAC 段换成垃圾 → 同一个 EncString 形状、MAC 校验必失败
        var good = Enc("eeb8ee82-ba9a-47e5-b74b-d2f768209988");
        var broken = good[..good.LastIndexOf('|')] + "|" + Convert.ToBase64String(new byte[32]);

        var json = ServerPayload(credentialId: broken);
        var c = Decode(json);

        Assert.Empty(c.Fido2Credentials!);                 // 坏的被丢掉……
        Assert.Equal("Google", c.Name);                    // ……但条目本身还在
        Assert.Equal("me@gmail.com", c.Login!.Username);
    }

    /// <summary>必需的 credentialId 解出来是空的 → 这枚凭据没法用，丢掉（但不影响条目）。</summary>
    [Fact]
    public void Passkey_Without_CredentialId_Is_Dropped()
    {
        var c = Decode(ServerPayload(credentialId: Enc("")));

        Assert.Empty(c.Fido2Credentials!);
        Assert.NotNull(c.Login);
    }

    /// <summary>没有 fido2 的普通条目照旧（别把 null 当成空数组又写出什么东西）。</summary>
    [Fact]
    public void Cipher_Without_Passkeys_Stays_Empty()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString(),
            type = 1,
            name = Enc("GitHub"),
            revisionDate = "2026-05-05T00:00:00Z",
            login = new { username = Enc("octocat") },
        });

        Assert.Null(Decode(json).Fido2Credentials);
    }
}
