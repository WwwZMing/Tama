using System.Globalization;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Services.Crypto;

namespace Tama.Services.Bitwarden;

/// <summary>
/// 本地 <see cref="Cipher"/> ↔ Bitwarden 的**唯一一份**双向字段映射。
///
/// 为什么要收成一份：以前上行（构造请求体）在 CipherService 里写了两遍、下行（同步响应落库）
/// 在 AuthService 里又写了一遍，三份都只填了 Login。结果四种类型里三种是"模型有列、代码不填"——
/// 表现为拉下来是空壳、推上去丢内容，而且加一个字段要记得改三个地方。
///
/// ⚠ 已知的有损字段（本地模型是 Bitwarden 的有损子集，补列之前无法避免，都在这里显式处理）：
///   · 身份信息：Bitwarden 有 Title / 中间名 / 地址2、3 / 市 / 省 / 邮编 / 国家 / 公司，
///     本地只有一个 <c>Address</c>。<see cref="CanStoreLocally"/> 会把装了这些字段的条目判为
///     "装不下"，调用方据此跳过（**不许静默压扁用户地址**）。
///   · 登录网址：Bitwarden 的每个 uri 还带 match 匹配方式，本地只存 uri 字符串本身。
///     拉的时候丢、推的时候按默认匹配方式发回去——属于历史遗留，补列前只能记在这里。
/// </summary>
public static class CipherMapper
{
    // ═══════════════════════ 下行：Bitwarden 响应 → 本地实体 ═══════════════════════

    /// <summary>
    /// 这份服务器数据本地模型装得下吗？装不下就别拉——否则以后任何一次编辑/收藏
    /// 都会把服务器上的原字段按本地（残缺的）形状推回去，等于静默损坏用户数据。
    /// </summary>
    public static bool CanStoreLocally(BitwardenCipherResponse r)
    {
        if (r.Type != (int)CipherType.Identity || r.Identity == null) return true;

        var i = r.Identity;
        return Blank(i.Title) && Blank(i.MiddleName)
            && Blank(i.Address2) && Blank(i.Address3)
            && Blank(i.City) && Blank(i.State) && Blank(i.PostalCode)
            && Blank(i.Country) && Blank(i.Company);

        static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
    }

    /// <summary>装不下时给用户看的理由（进同步结果，不吞掉）。</summary>
    public static string? DescribeSkip(BitwardenCipherResponse r) =>
        r.Type == (int)CipherType.Identity && !CanStoreLocally(r)
            ? "身份信息里有本地模型装不下的字段（中间名/地址多行/省市邮编/公司等）"
            : null;

    /// <summary>
    /// 本地这一行**缺了**服务器那份有的东西吗？
    ///
    /// 这是给"RevisionDate 相等就跳过重写"那个优化打的补丁（2026-09-13 实测踩到）：
    /// RevisionDate 只说明"内容版本相同"，而**本地这份是哪个版本的代码写进去的**是另一件事。
    /// 实测：用户那 198 行是 <see cref="CipherMapper"/> 出现之前的旧内联映射写的（只填 Login/Notes），
    /// 而跳过逻辑与 CipherMapper 是同一个提交进来的 → 从那以后每次同步都报 "198 unchanged"，
    /// 卡片 / 自定义字段 / 通行密钥在本地**永远补不回来**。表面症状是主页面「通行密钥」筛选恒为 0、
    /// 点不动（那个分面数的是本地行）；更危险的是本地缺字段时点一次编辑，
    /// <see cref="ToRemoteBody"/> 会把缺失的段落按 null 推回去 = 静默删掉服务器上的卡片/通行密钥。
    ///
    /// 判据的取舍：
    ///   · **只判"缺"不判"等"**——本地更全时不重写（宁可少写，不可多写）。
    ///   · 一律用**能可靠读回来**的形态：自有集合看条数；可空依赖看"有没有非空字段"。
    ///     直接判 `local.Card == null` 会踩 EF 的坑：表共享的 optional dependent 在"所有列都是 null"
    ///     时查询不 materialize，于是每一轮都判成"缺"→ 每轮重写 → Updated 永远非 0，
    ///     定时拉就会一直弹"已从云端同步"。
    /// </summary>
    public static bool LocalIsMissingData(Cipher local, BitwardenCipherResponse remote)
    {
        if ((remote.Fields?.Count ?? 0) > (local.Fields?.Count ?? 0)) return true;
        if ((remote.Fido2Credentials?.Count ?? 0) > (local.Fido2Credentials?.Count ?? 0)) return true;

        var card = remote.Card;
        if (card != null && local.Card == null &&
            Any(card.CardholderName, card.Number, card.Brand, card.ExpMonth, card.ExpYear, card.Code))
            return true;

        var identity = remote.Identity;
        if (identity != null && local.Identity == null &&
            Any(identity.FirstName, identity.LastName, identity.Email, identity.Phone,
                identity.Ssn, identity.Username, identity.Address1))
            return true;

        var login = remote.Login;
        if (login != null && local.Login == null &&
            (Any(login.Username, login.Password, login.Totp) || login.Uris.Count > 0))
            return true;

        // SecureNote 刻意不判：本地 CipherNote 没有可靠读回来的列（正文在 Notes 里，没有丢失风险），
        // 判了就会每轮重写。
        return false;

        static bool Any(params string?[] values) => values.Any(v => !string.IsNullOrEmpty(v));
    }

    /// <summary>把服务器响应的内容整体写进本地实体（四种类型 + 自定义字段 + 通行密钥）。</summary>
    public static void ApplyRemote(Cipher target, BitwardenCipherResponse r)
    {
        target.Type = (CipherType)r.Type;
        target.Name = r.Name ?? "";
        target.Notes = r.Notes;
        target.Favorite = r.Favorite;
        target.FolderId = Guid.TryParse(r.FolderId, out var folderId) ? folderId : null;
        target.CreatedAt = r.CreatedDate;
        target.UpdatedAt = r.RevisionDate;
        target.DeletedAt = r.DeletedDate;
        target.SyncStatus = "synced";
        target.PendingOp = null;
        target.RetryCount = 0;
        target.LastAttempt = null;

        target.Login = r.Login == null ? null : new CipherLogin
        {
            Username = r.Login.Username,
            Password = r.Login.Password,
            Totp = r.Login.Totp,
            Uris = (r.Login.Uris ?? new()).Select(u => u.Uri ?? "").Where(u => u.Length > 0).ToList(),
        };

        target.Card = r.Card == null ? null : new CipherCard
        {
            CardholderName = r.Card.CardholderName,
            Number = r.Card.Number,
            Brand = r.Card.Brand,
            ExpMonth = r.Card.ExpMonth,
            ExpYear = r.Card.ExpYear,
            Code = r.Card.Code,
        };

        target.Identity = r.Identity == null ? null : new CipherIdentity
        {
            FirstName = r.Identity.FirstName,
            LastName = r.Identity.LastName,
            Email = r.Identity.Email,
            Phone = r.Identity.Phone,
            Ssn = r.Identity.Ssn,
            Username = r.Identity.Username,
            Address = JoinAddress(r.Identity),
        };

        // 安全笔记：Bitwarden 的 secureNote **只有 type 没有正文**，正文在 notes 里（已随 Notes 落库）。
        // 本地那个 CipherNote.Text 是历史字段，没有任何代码读它——这里只留个占位，
        // 绝不把正文再抄一份进去（抄两份就是"同一个东西两种放法"的老毛病）。
        target.SecureNote = r.SecureNote == null && r.Type != (int)CipherType.SecureNote
            ? null
            : new CipherNote();

        // 自定义字段与通行密钥同理：Id 是复合主键 (CipherId, Id) 的后半截，按条目内 0,1,2… 编号
        target.Fields = (r.Fields ?? new()).Select((f, i) => new CipherField
        {
            Id = i,
            Name = f.Name ?? "",
            Value = f.Value,
            Type = f.Type,
            Hidden = f.Hidden,
        }).ToList();

        // 通行密钥：同上
        target.Fido2Credentials = (r.Fido2Credentials ?? new()).Select((f, i) => new Fido2Credential
        {
            Id = i,
            CredentialId = f.CredentialId ?? "",
            KeyType = f.KeyType ?? "public-key",
            KeyAlgorithm = f.KeyAlgorithm ?? "ECDSA",
            KeyCurve = f.KeyCurve ?? "P-256",
            KeyValue = f.KeyValue ?? "",
            RpId = f.RpId ?? "",
            RpName = f.RpName,
            UserName = f.UserName,
            UserHandle = f.UserHandle,
            UserDisplayName = f.UserDisplayName,
            Counter = f.Counter,
            Discoverable = f.Discoverable,
            CreationDate = f.CreationDate,
        }).ToList();
    }

    /// <summary>把 Bitwarden 的多段地址拼成一行（本地只有一个地址字段）。</summary>
    private static string? JoinAddress(BitwardenIdentityData i)
    {
        var parts = new[] { i.Address1, i.Address2, i.Address3, i.City, i.State, i.PostalCode, i.Country }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim());
        var joined = string.Join(", ", parts);
        return joined.Length > 0 ? joined : null;
    }

    // ═══════════════════════ 上行：本地实体 → Bitwarden 请求体（加密） ═══════════════════════

    /// <summary>
    /// 由**本地实体**构造 <c>POST/PUT /ciphers</c> 的请求体，四种类型都加密带上。
    /// 关键：入参是本地实体而不是"UI 传来的请求 DTO"——DTO 里没有卡片/身份字段，
    /// 拿它构造请求体等于把服务器上的卡片清空（收藏一下就会发生）。
    /// </summary>
    public static object ToRemoteBody(Cipher cipher, byte[] userEncKey, byte[] userMacKey)
    {
        var cipherKey = BitwardenCrypto.GenerateCipherKey();
        var (encKey, macKey) = BitwardenCrypto.SplitKey(cipherKey);

        string? E(string? s) => string.IsNullOrEmpty(s) ? null : BitwardenCrypto.EncryptString(s, encKey, macKey);

        // 通行密钥也是 login 的一部分，必须跟着一起发。⚠ 判空不能只看 cipher.Login：
        // 一枚"只有通行密钥、没有用户名/密码/网址"的凭据，本地读回来时 Login 可能是 null
        // （表共享的 optional dependent 在所有列都是 null 时不 materialize），
        // 那种条目如果漏发 fido2Credentials，服务端就会把它那枚通行密钥抹掉。
        var fido2 = Fido2ToRemote(cipher, E);
        object? login = cipher.Login == null && fido2 == null ? null : new
        {
            username = E(cipher.Login?.Username),
            password = E(cipher.Login?.Password),
            totp = E(cipher.Login?.Totp),
            uris = cipher.Login?.Uris?.Select(u => new { uri = E(u) }).ToList(),
            fido2Credentials = fido2,
        };

        object? card = cipher.Card == null ? null : new
        {
            cardholderName = E(cipher.Card.CardholderName),
            number = E(cipher.Card.Number),
            brand = E(cipher.Card.Brand),
            expMonth = E(cipher.Card.ExpMonth),
            expYear = E(cipher.Card.ExpYear),
            code = E(cipher.Card.Code),
        };

        // 身份信息只回推本地装得下的那几个字段；多段地址按单行发回（Address1）。
        // 见类注释：装了 Address2/City/… 的条目会被 CanStoreLocally 挡在拉取之外，
        // 所以这里不会把服务器上已有的多段地址压扁。
        object? identity = cipher.Identity == null ? null : new
        {
            firstName = E(cipher.Identity.FirstName),
            lastName = E(cipher.Identity.LastName),
            email = E(cipher.Identity.Email),
            phone = E(cipher.Identity.Phone),
            ssn = E(cipher.Identity.Ssn),
            username = E(cipher.Identity.Username),
            address1 = E(cipher.Identity.Address),
        };

        // 安全笔记：Bitwarden 要 secureNote.type（0=通用），正文照旧走 notes
        object? secureNote = cipher.Type == CipherType.SecureNote || cipher.SecureNote != null
            ? new { type = 0 }
            : null;

        var fields = cipher.Fields is { Count: > 0 }
            ? cipher.Fields.Select(f => new
            {
                name = E(f.Name),
                value = E(f.Value),
                type = f.Type,
                hidden = f.Hidden,
            }).ToList<object>()
            : null;

        var body = new
        {
            type = (int)cipher.Type,
            name = BitwardenCrypto.EncryptString(cipher.Name ?? "", encKey, macKey),
            notes = E(cipher.Notes),
            key = BitwardenCrypto.EncryptCipherKey(cipherKey, userEncKey, userMacKey),
            favorite = cipher.Favorite,
            folderId = cipher.FolderId?.ToString(),
            login,
            card,
            identity,
            secureNote,
            fields,
        };

        return body;
    }

    /// <summary>
    /// 上行：本地通行密钥 → Bitwarden 的 <c>login.fido2Credentials[]</c>。
    ///
    /// **必须发**：服务端的 <c>CipherRequestModel.ToCipher</c> 是**整体替换** cipher 的 Data
    /// （`existingCipher.Data = JsonSerializer.Serialize(loginData, IgnoreWritingNull)`），
    /// 没带的段落会被整个省掉——所以漏发 fido2Credentials 等于**静默删除用户服务器上的通行密钥**。
    /// 以前这里压根没有这一段，于是"在 Tama 里改一下带通行密钥的条目"就会毁掉那枚凭据。
    ///
    /// 格式（对照 bitwarden/clients 的 <c>cipher.request.ts</c>）：除 <c>creationDate</c> 外
    /// **所有字段都是用条目密钥加密的 EncString**，包括 counter（"0"）和 discoverable（"true"）——
    /// 它们是**字符串的密文**，不是数字/布尔。
    /// </summary>
    private static List<object>? Fido2ToRemote(Cipher cipher, Func<string?, string?> encrypt)
    {
        if (cipher.Fido2Credentials is not { Count: > 0 }) return null;

        return cipher.Fido2Credentials.Select(f => (object)new
        {
            credentialId = encrypt(f.CredentialId),
            keyType = encrypt(f.KeyType),
            keyAlgorithm = encrypt(f.KeyAlgorithm),
            keyCurve = encrypt(f.KeyCurve),
            keyValue = encrypt(f.KeyValue),
            rpId = encrypt(f.RpId),
            rpName = encrypt(f.RpName),
            userHandle = encrypt(f.UserHandle),
            userName = encrypt(f.UserName),
            userDisplayName = encrypt(f.UserDisplayName),
            counter = encrypt(f.Counter.ToString(CultureInfo.InvariantCulture)),
            discoverable = encrypt(f.Discoverable ? "true" : "false"),
            creationDate = AsUtcIso(f.CreationDate),
        }).ToList();
    }

    /// <summary>
    /// 明文 ISO 8601（UTC）——creationDate 是这一组里唯一不加密的字段。
    /// Kind 是 Unspecified 时**当它是 UTC**（值来自服务器 JSON，本来就是 UTC），
    /// 别走 ToUniversalTime()：那会按本机时区平移，把凭据的创建时间改掉。
    /// </summary>
    private static string AsUtcIso(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
}
