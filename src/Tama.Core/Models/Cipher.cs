namespace Tama.Core.Models;

public class Cipher
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CipherType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool Favorite { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
    public Guid? FolderId { get; set; }
    public CipherLogin? Login { get; set; }
    public CipherCard? Card { get; set; }
    public CipherIdentity? Identity { get; set; }
    public CipherNote? SecureNote { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<Fido2Credential>? Fido2Credentials { get; set; }
    public List<CipherField>? Fields { get; set; }
    public string SyncStatus { get; set; } = "synced";
    public string? PendingOp { get; set; }
    public int RetryCount { get; set; } = 0;
    public DateTime? LastAttempt { get; set; }

    /// <summary>
    /// 深拷贝成一条**新键**的条目（各 section 都是新实例，能直接 Add 进另一个跟踪上下文）。
    ///
    /// 为什么要这个：离线新建的条目用的是本地 Guid，推送成功后要把主键换成服务器 ID，而
    /// **EF 不允许修改已跟踪实体的键属性**（"The property 'Cipher.Id' is part of a key and so
    /// cannot be modified"，实测 EF 11），直接赋值会在下一次 SaveChanges 上炸掉。
    /// 正确做法是"删旧行 + 以新键插入新行"，所以需要一份内容完整的拷贝。
    /// </summary>
    public Cipher DeepCopyWithId(Guid newId) => new()
    {
        Id = newId,
        Type = Type,
        Name = Name,
        Notes = Notes,
        Favorite = Favorite,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        DeletedAt = DeletedAt,
        FolderId = FolderId,
        Tags = new List<string>(Tags),
        SyncStatus = "synced",     // 换键只发生在"刚推成功"这一种场景
        PendingOp = null,
        RetryCount = 0,
        LastAttempt = null,
        Login = Login == null ? null : new CipherLogin
        {
            Username = Login.Username,
            Password = Login.Password,
            Totp = Login.Totp,
            Uris = new List<string>(Login.Uris ?? new()),
        },
        Card = Card == null ? null : new CipherCard
        {
            CardholderName = Card.CardholderName,
            Number = Card.Number,
            Brand = Card.Brand,
            ExpMonth = Card.ExpMonth,
            ExpYear = Card.ExpYear,
            Code = Card.Code,
        },
        Identity = Identity == null ? null : new CipherIdentity
        {
            FirstName = Identity.FirstName,
            LastName = Identity.LastName,
            Email = Identity.Email,
            Phone = Identity.Phone,
            Address = Identity.Address,
            Ssn = Identity.Ssn,
            Username = Identity.Username,
        },
        SecureNote = SecureNote == null ? null : new CipherNote { Text = SecureNote.Text },
        // 自有集合必须造新实例（同一实例被两条实体引用会让 EF 跟踪器发疯），Id 照抄即可
        Fields = Fields?.Select(f => new CipherField
        {
            Id = f.Id, Name = f.Name, Value = f.Value, Type = f.Type, Hidden = f.Hidden,
        }).ToList(),
        Fido2Credentials = Fido2Credentials?.Select(f => new Fido2Credential
        {
            Id = f.Id, CredentialId = f.CredentialId, KeyType = f.KeyType, KeyAlgorithm = f.KeyAlgorithm,
            KeyCurve = f.KeyCurve, KeyValue = f.KeyValue, RpId = f.RpId, RpName = f.RpName,
            UserName = f.UserName, UserHandle = f.UserHandle, UserDisplayName = f.UserDisplayName,
            Counter = f.Counter, Discoverable = f.Discoverable, CreationDate = f.CreationDate,
        }).ToList(),
    };
}

public class CipherField
{
    /// <summary>
    /// 复合主键 (CipherId, Id) 的后半截：**同一 Cipher 内从 0 开始编号**，由写入方赋值。
    /// 与 <see cref="Fido2Credential.Id"/> 同一个坑、同一个理由：复合主键里的 int 列 SQLite 不会生成
    /// （只有 INTEGER PRIMARY KEY 作为 rowid 别名时才会），而 EF 默认把它当 store-generated
    /// 从 INSERT 里省掉 → NOT NULL constraint failed: CipherField.Id。表结构不能改（无迁移机制）。
    /// </summary>
    public int Id { get; set; }

    public string Name { get; set; } = "";
    public string? Value { get; set; }
    public int Type { get; set; } = 0;
    public bool Hidden { get; set; }
}

public class Fido2Credential
{
    /// <summary>
    /// 复合主键 (CipherId, Id) 的后半截：**同一 Cipher 内从 0 开始编号**，由写入方赋值。
    ///
    /// 为什么不是数据库生成：主键是复合的，SQLite 不会为其中的 int 列自动生成值（只有
    /// INTEGER PRIMARY KEY 作为 rowid 别名时才会）。EF 若把 Id 当 store-generated，
    /// 就会从 INSERT 里省掉这一列 → NOT NULL constraint failed: Fido2Credential.Id。
    /// 表结构不能改（宿主用 EnsureCreated，老库不会迁移），所以只能让调用方给值。
    /// </summary>
    public int Id { get; set; }

    public string CredentialId { get; set; } = "";
    public string KeyType { get; set; } = "public-key";
    public string KeyAlgorithm { get; set; } = "ECDSA";
    public string KeyCurve { get; set; } = "P-256";
    public string KeyValue { get; set; } = "";
    public string RpId { get; set; } = "";
    public string? RpName { get; set; }
    public string? UserName { get; set; }
    public string? UserHandle { get; set; }
    public string? UserDisplayName { get; set; }
    public int Counter { get; set; }
    public bool Discoverable { get; set; }
    public DateTime CreationDate { get; set; } = DateTime.UtcNow;
}
