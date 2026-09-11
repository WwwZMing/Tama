using System.Text.Json;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Services.Bitwarden;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// 本地 Cipher ↔ Bitwarden 的映射契约。
///
/// 这些用例是冲着"同一个模型写三遍、每遍只填 Login"那个老毛病去的：
/// 以前上传的请求体只带 login、下载的落库也只写 login，于是卡片 / 身份 / 安全笔记 /
/// 自定义字段在**两个方向上都丢**，而且谁都没发现——因为没有任何测试碰过这些类型。
///
/// 断言方式是"往返"：本地实体 → 请求体（加密）→ 序列化成 JSON →
/// 反序列化回 raw（等同服务端 payload）→ 走**真实的** DecryptCipher → 再落回本地实体。
/// 加类型、加字段、改错 JSON 属性名，这里立刻红。
/// （能这么写是因为 Tama.Services 对测试开了 InternalsVisibleTo，见 csproj 注释。）
/// </summary>
public class CipherMapperTests
{
    private static readonly byte[] EncKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] MacKey = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();

    /// <summary>模拟一次"上传到服务器、再同步回来"：请求体 JSON 当服务端 payload 解。</summary>
    private static Cipher RoundTripThroughServer(Cipher local)
    {
        var body = CipherMapper.ToRemoteBody(local, EncKey, MacKey);
        var json = JsonSerializer.Serialize(body);
        var raw = JsonSerializer.Deserialize<BitwardenRawCipher>(json)!;

        var remote = BitwardenApiClient.DecryptCipher(raw, EncKey, MacKey, new());

        var back = new Cipher { Id = local.Id };
        CipherMapper.ApplyRemote(back, remote);
        return back;
    }

    // ───────────────────────────── 各种类型 ─────────────────────────────

    [Fact]
    public void Login_Roundtrips_All_Fields()
    {
        var local = new Cipher
        {
            Type = CipherType.Login,
            Name = "GitHub",
            Notes = "备注",
            Favorite = true,
            Login = new CipherLogin
            {
                Username = "octocat",
                Password = "s3cret",
                Totp = "JBSWY3DPEHPK3PXP",
                Uris = new() { "https://github.com", "https://gist.github.com" },
            },
        };

        var back = RoundTripThroughServer(local);

        Assert.Equal(CipherType.Login, back.Type);
        Assert.Equal("GitHub", back.Name);
        Assert.Equal("备注", back.Notes);
        Assert.True(back.Favorite);
        Assert.Equal("octocat", back.Login!.Username);
        Assert.Equal("s3cret", back.Login.Password);
        Assert.Equal("JBSWY3DPEHPK3PXP", back.Login.Totp);
        Assert.Equal(new[] { "https://github.com", "https://gist.github.com" }, back.Login.Uris.ToArray());
        Assert.Equal("synced", back.SyncStatus);
    }

    /// <summary>这条正是"以前上传一张卡片、服务器收到的是空卡片"的回归。</summary>
    [Fact]
    public void Card_Roundtrips_All_Fields()
    {
        var local = new Cipher
        {
            Type = CipherType.Card,
            Name = "Visa",
            Card = new CipherCard
            {
                CardholderName = "ME",
                Number = "4111111111111111",
                Brand = "Visa",
                ExpMonth = "07",
                ExpYear = "2030",
                Code = "123",
            },
        };

        var back = RoundTripThroughServer(local);

        Assert.Equal(CipherType.Card, back.Type);
        Assert.NotNull(back.Card);
        Assert.Equal("ME", back.Card!.CardholderName);
        Assert.Equal("4111111111111111", back.Card.Number);
        Assert.Equal("Visa", back.Card.Brand);
        Assert.Equal("07", back.Card.ExpMonth);
        Assert.Equal("2030", back.Card.ExpYear);
        Assert.Equal("123", back.Card.Code);
    }

    [Fact]
    public void SecureNote_Keeps_Body_In_Notes_Only()
    {
        var local = new Cipher
        {
            Type = CipherType.SecureNote,
            Name = "WiFi",
            Notes = "密码是 hunter2",
        };

        var back = RoundTripThroughServer(local);

        Assert.Equal(CipherType.SecureNote, back.Type);
        Assert.Equal("密码是 hunter2", back.Notes);
        Assert.NotNull(back.SecureNote);              // secureNote.type 要带上，否则服务器不认这是笔记
        Assert.Null(back.SecureNote!.Text);           // 正文只存一处（CipherNote.Text 是历史字段）
    }

    [Fact]
    public void Identity_Simple_Roundtrips()
    {
        var local = new Cipher
        {
            Type = CipherType.Identity,
            Name = "我的身份",
            Identity = new CipherIdentity
            {
                FirstName = "San",
                LastName = "Zhang",
                Email = "san@example.com",
                Phone = "13800000000",
                Ssn = "110101199001011234",
                Username = "san",
                Address = "北京市朝阳区某路 1 号",
            },
        };

        var back = RoundTripThroughServer(local);

        Assert.Equal(CipherType.Identity, back.Type);
        Assert.NotNull(back.Identity);
        Assert.Equal("San", back.Identity!.FirstName);
        Assert.Equal("Zhang", back.Identity.LastName);
        Assert.Equal("san@example.com", back.Identity.Email);
        Assert.Equal("13800000000", back.Identity.Phone);
        Assert.Equal("北京市朝阳区某路 1 号", back.Identity.Address);
    }

    [Fact]
    public void Custom_Fields_Roundtrip()
    {
        var local = new Cipher
        {
            Type = CipherType.Login,
            Name = "带自定义字段",
            Fields = new()
            {
                new CipherField { Id = 0, Name = "PIN", Value = "1234", Type = 1, Hidden = true },
                new CipherField { Id = 1, Name = "备注字段", Value = "hello", Type = 0, Hidden = false },
            },
        };

        var back = RoundTripThroughServer(local);

        Assert.NotNull(back.Fields);
        Assert.Equal(2, back.Fields!.Count);
        Assert.Equal("PIN", back.Fields[0].Name);
        Assert.Equal("1234", back.Fields[0].Value);
        Assert.Equal(1, back.Fields[0].Type);
        Assert.True(back.Fields[0].Hidden);
        Assert.Equal("hello", back.Fields[1].Value);
        // 编号必须由映射器给出（复合主键 (CipherId, Id)）：忘编就会 NOT NULL constraint failed
        Assert.Equal(new[] { 0, 1 }, back.Fields.Select(f => f.Id).ToArray());
    }

    // ───────────────────────── 装不下的条目：不许静默压扁 ─────────────────────────

    [Fact]
    public void Identity_With_Fields_Local_Model_Cannot_Hold_Is_Refused()
    {
        var simple = new BitwardenCipherResponse
        {
            Type = (int)CipherType.Identity,
            Identity = new BitwardenIdentityData { FirstName = "A", Address1 = "某路 1 号" },
        };
        var rich = new BitwardenCipherResponse
        {
            Type = (int)CipherType.Identity,
            Name = "复杂身份",
            Identity = new BitwardenIdentityData { FirstName = "A", City = "北京", PostalCode = "100000" },
        };
        var richCompany = new BitwardenCipherResponse
        {
            Type = (int)CipherType.Identity,
            Identity = new BitwardenIdentityData { Company = "ACME" },
        };

        Assert.True(CipherMapper.CanStoreLocally(simple));
        Assert.False(CipherMapper.CanStoreLocally(rich));         // 会丢市/邮编
        Assert.False(CipherMapper.CanStoreLocally(richCompany));  // 会丢公司
        Assert.NotNull(CipherMapper.DescribeSkip(rich));
    }

    [Fact]
    public void Non_Identity_Types_Are_Always_Storable()
    {
        Assert.True(CipherMapper.CanStoreLocally(new BitwardenCipherResponse { Type = (int)CipherType.Login }));
        Assert.True(CipherMapper.CanStoreLocally(new BitwardenCipherResponse { Type = (int)CipherType.Card }));
        Assert.True(CipherMapper.CanStoreLocally(new BitwardenCipherResponse { Type = (int)CipherType.SecureNote }));
    }

    // ───────────────────────── ApplyRemote 的其它落点 ─────────────────────────

    [Fact]
    public void ApplyRemote_Numbers_Passkeys_From_Zero()
    {
        // 复合主键 (CipherId, Id) 要求写入方自己编号，忘编会当场炸（见 Fido2CredentialPersistenceTests）
        var remote = new BitwardenCipherResponse
        {
            Type = (int)CipherType.Login,
            Name = "带通行密钥",
            Fido2Credentials = new()
            {
                new BitwardenFido2Credential { CredentialId = "a", KeyValue = "k1" },
                new BitwardenFido2Credential { CredentialId = "b", KeyValue = "k2" },
            },
        };

        var back = new Cipher { Id = Guid.NewGuid() };
        CipherMapper.ApplyRemote(back, remote);

        Assert.Equal(new[] { 0, 1 }, back.Fido2Credentials!.Select(f => f.Id).ToArray());
        Assert.Equal("k1", back.Fido2Credentials![0].KeyValue);
        Assert.Equal("a", back.Fido2Credentials[0].CredentialId);
    }

    // ───────────────────────── 通行密钥（上行必须发、且格式是"除时间外全密文"） ─────────────────────────

    private static readonly DateTime CredCreated = new(2026, 5, 5, 1, 2, 3, DateTimeKind.Utc);

    private static Cipher LoginWithPasskey() => new()
    {
        Type = CipherType.Login,
        Name = "Google",
        Login = new CipherLogin { Username = "me@gmail.com" },
        Fido2Credentials = new()
        {
            new Fido2Credential
            {
                Id = 0,
                CredentialId = "eeb8ee82-ba9a-47e5-b74b-d2f768209988",
                KeyType = "public-key",
                KeyAlgorithm = "ECDSA",
                KeyCurve = "P-256",
                KeyValue = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg",
                RpId = "google.com",
                RpName = "Google",
                UserName = "me@gmail.com",
                UserHandle = "R09PR0xFX0FDQ09VTlQ6MTAwMA",
                UserDisplayName = "Me",
                Counter = 7,
                Discoverable = true,
                CreationDate = CredCreated,
            },
        },
    };

    /// <summary>
    /// 通行密钥的往返：本地实体 → 请求体（加密）→ 当服务端 payload 解回来 → 落回本地实体。
    ///
    /// 这条守的是"漏发 fido2Credentials = 静默删掉服务器上的通行密钥"：服务端
    /// <c>CipherRequestModel.ToCipher</c> 是**整体替换** cipher 的 Data（`IgnoreWritingNull`），
    /// 没带的段落会被整个省掉。以前上行压根没有这一段。
    ///
    /// 顺带把 wire 格式钉死：除 creationDate 外**每个字段都是 EncString**（包括 counter/discoverable
    /// ——它们是"0"/"true" 的**密文**，不是数字/布尔；写成 int 会让带通行密钥的账号一同步就整体失败）。
    /// </summary>
    [Fact]
    public void Fido2_Roundtrips_All_Fields()
    {
        var back = RoundTripThroughServer(LoginWithPasskey());

        var f = Assert.Single(back.Fido2Credentials!);
        Assert.Equal(0, f.Id);
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
        Assert.Equal(CredCreated, f.CreationDate);
    }

    [Fact]
    public void Fido2_Up_Push_Is_Encrypted_And_Nested_Under_Login()
    {
        var json = JsonSerializer.Serialize(CipherMapper.ToRemoteBody(LoginWithPasskey(), EncKey, MacKey));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 位置：必须在 login 里面（曾经写在 cipher 层 → 反序列化永远绑不到 → 同步下来一枚都没有）
        Assert.False(root.TryGetProperty("fido2Credentials", out _));
        var cred = root.GetProperty("login").GetProperty("fido2Credentials")[0];

        // 形状：除 creationDate 外全是 EncString（前缀 "2."）
        Assert.StartsWith("2.", cred.GetProperty("credentialId").GetString());
        Assert.StartsWith("2.", cred.GetProperty("keyType").GetString());
        Assert.StartsWith("2.", cred.GetProperty("keyAlgorithm").GetString());
        Assert.StartsWith("2.", cred.GetProperty("keyCurve").GetString());
        Assert.StartsWith("2.", cred.GetProperty("keyValue").GetString());
        Assert.StartsWith("2.", cred.GetProperty("rpId").GetString());
        Assert.StartsWith("2.", cred.GetProperty("rpName").GetString());
        Assert.StartsWith("2.", cred.GetProperty("userHandle").GetString());
        Assert.StartsWith("2.", cred.GetProperty("userName").GetString());
        Assert.StartsWith("2.", cred.GetProperty("userDisplayName").GetString());
        Assert.StartsWith("2.", cred.GetProperty("counter").GetString());        // "7" 的密文
        Assert.StartsWith("2.", cred.GetProperty("discoverable").GetString());  // "true" 的密文
        Assert.Equal("2026-05-05T01:02:03.0000000Z", cred.GetProperty("creationDate").GetString());
    }

    /// <summary>
    /// 一枚"只有通行密钥"的条目：本地读回来时 <c>Login</c> 可能是 null（表共享的 optional dependent
    /// 在所有列都是 null 时不 materialize）。判空要是只看 <c>cipher.Login</c>，这种条目就会整段漏发
    /// fido2Credentials → 服务端把那枚通行密钥抹掉。
    /// </summary>
    [Fact]
    public void Fido2_Is_Sent_Even_When_Local_Login_Is_Null()
    {
        var local = LoginWithPasskey();
        local.Login = null;

        var json = JsonSerializer.Serialize(CipherMapper.ToRemoteBody(local, EncKey, MacKey));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(1, doc.RootElement.GetProperty("login").GetProperty("fido2Credentials").GetArrayLength());
    }

    [Fact]
    public void ApplyRemote_Marks_Synced_And_Clears_Pending_State()    {
        var revision = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        var back = new Cipher
        {
            Id = Guid.NewGuid(),
            SyncStatus = "failed",
            PendingOp = "update",
            RetryCount = 9,
            LastAttempt = DateTime.UtcNow,
        };

        CipherMapper.ApplyRemote(back, new BitwardenCipherResponse
        {
            Type = (int)CipherType.Login,
            Name = "x",
            RevisionDate = revision,
        });

        Assert.Equal("synced", back.SyncStatus);
        Assert.Null(back.PendingOp);
        Assert.Equal(0, back.RetryCount);
        Assert.Null(back.LastAttempt);
        Assert.Equal(revision, back.UpdatedAt);
    }

    [Fact]
    public void ApplyRemote_Clears_Sections_That_Server_No_Longer_Has()
    {
        // 服务器上把卡片字段清空了 → 本地也要跟着清，不能留旧值（否则两边长期不一致）
        var back = new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.Card,
            Card = new CipherCard { Number = "4111111111111111" },
        };

        CipherMapper.ApplyRemote(back, new BitwardenCipherResponse
        {
            Type = (int)CipherType.Login,
            Name = "改成登录项了",
            Login = new BitwardenLoginData { Username = "u" },
        });

        Assert.Null(back.Card);
        Assert.Equal(CipherType.Login, back.Type);
        Assert.Equal("u", back.Login!.Username);
    }
}
