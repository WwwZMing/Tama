namespace Tama.Core.Contracts;

/// <summary>
/// 保险库条目：查询、增删改、明文导出。
/// 实现：<c>Tama.Services.Vault.CipherService</c>。
/// </summary>
public interface IVaultApi
{
    /// <summary>按条件查询条目 + 文件夹列表（两者由同一次调用返回，避免页面发两次请求）。</summary>
    Task<CipherSearchResponse> Search(CipherSearchRequest? req);

    /// <summary>
    /// 取单条（含明文密码/TOTP 等敏感字段）。列表页只需展示名称/用户名，
    /// 详情与编辑一律用这个方法按需取——别再"搜全库再挑一条"，那会把整个保险库
    /// 拉进页面状态。找不到（已删除/Id 非法）返回 null，不抛异常。
    /// </summary>
    Task<CipherDto?> Get(string id);

    /// <summary>新建条目；离线或推送失败时条目以 pending 状态落库，交给后台 SyncWorker 重试。</summary>
    Task<CipherCreateResponse> Create(CreateCipherRequest req);

    /// <summary>更新条目（全量覆盖语义：<c>Login</c>/<c>Favorite</c> 等未带字段会被重置，调用方需传完整对象）。</summary>
    Task Update(UpdateCipherRequest req);

    /// <summary>
    /// 只切换收藏。**别用 <see cref="Update"/> 代替**：那是整条覆盖语义，而页面手上的
    /// <see cref="CipherDto"/> 根本没有卡片/身份/自定义字段——拿它去覆盖会把服务器上那些内容清空。
    /// 这个方法从本地实体出发，不会丢任何字段。
    /// </summary>
    Task SetFavorite(string id, bool favorite);

    /// <summary>删除条目。<paramref name="softDelete"/> 为 true 时走回收站语义（同步时用 Trash）。</summary>
    Task Delete(string id, bool softDelete = false);

    /// <summary>导出为明文 JSON 并落盘到下载目录（含敏感信息，UI 必须警示用户）。</summary>
    Task<VaultExportResponse> Export();

    /// <summary>
    /// 导出为 **Bitwarden 的未加密个人保险库 JSON**，供 Bitwarden 官网「工具 → 导入数据」
    /// 选 <c>Bitwarden (json)</c> 直接吃下。与 <see cref="Export"/> 的区别：那个是自家备份、
    /// 能无损导回；这个是**单向投递**格式，Bitwarden 装不下的字段（通行密钥、标签等）会丢。
    /// </summary>
    Task<VaultExportResponse> ExportBitwarden();
}

/// <summary>
/// 文件夹：列表与增删改。
/// 实现：<c>Tama.Services.Vault.FolderService</c>。
/// </summary>
public interface IFolderApi
{
    /// <summary>文件夹列表；有 Bitwarden 账号时并入云端缓存，本地表始终参与（离线也能建）。</summary>
    Task<List<FolderDto>> List(string? accountId = null);

    /// <summary>新建文件夹，返回（可能是云端分配的）新 Id。</summary>
    Task<FolderCreateResponse> Create(string name);

    /// <summary>重命名文件夹。</summary>
    Task Rename(string id, string name);

    /// <summary>删除文件夹（其中的条目变为未分组，不级联删除）。</summary>
    Task Delete(string id);
}

// === 请求 ===

/// <summary>
/// 条目查询过滤条件。
/// 语义：**同一栏内取并集，跨栏取交集**（选了文件夹还能再叠 TOTP，两者都要满足）。
/// 每栏为 null/空 = 该栏不限制。
/// </summary>
public sealed record CipherSearchRequest
{
    public string? AccountId { get; init; }

    /// <summary>条目类型多选（1=登录 2=笔记 3=银行卡 4=身份）；空 = 不限。</summary>
    public List<int>? Types { get; init; }

    /// <summary>
    /// 文件夹多选；**空字符串代表"未分类"**（Cipher.FolderId 为 null 的那些），空集合 = 不限。
    /// 用空串而不另开一个 IncludeUnfiled 开关：文件夹 Id 是 GUID 串，空串永远不会与真 Id 撞。
    /// </summary>
    public List<string>? FolderIds { get; init; }

    /// <summary>模糊匹配名称/用户名/URI/备注。</summary>
    public string? Query { get; init; }

    /// <summary>只看带 TOTP 密钥的条目</summary>
    public bool HasTotp { get; init; }

    /// <summary>只看带通行密钥（Fido2 凭据）的条目</summary>
    public bool HasPasskey { get; init; }
}

/// <summary>新建条目。</summary>
public sealed record CreateCipherRequest
{
    public string? AccountId { get; init; }
    public int Type { get; init; }
    public string? Name { get; init; }
    public string? Notes { get; init; }
    /// <summary>放进哪个文件夹（本地文件夹 Id，可空 = 未分类）。没有关联账号时是纯本地文件夹 Id。</summary>
    public string? FolderId { get; init; }
    public CipherLoginRequest? Login { get; init; }

    // === 以下为"还原"路径补的载荷（2026-09-21，导入 tama-json 备份用）===
    // 全部可空/有默认值，既有调用方（UI 新建、KeePass 导入）一行都不用改。
    // 没有它们的话，从备份导回来的银行卡/身份信息会退化成"类型对、内容空"的条目——
    // 正是 CipherMapper 注释里记的那个老毛病（推上去等于清空服务器上的卡片）。

    /// <summary>收藏状态。默认 false（UI 新建行为不变）；导入时按备份还原。</summary>
    public bool Favorite { get; init; }

    /// <summary>原始创建/修改时间。为 null 时用当前时间（UI 新建行为不变）。</summary>
    public DateTime? CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }

    /// <summary>银行卡载荷（Type=3）。只填与 Card 类型匹配的段落，其余留 null。</summary>
    public CipherCardRequest? Card { get; init; }

    /// <summary>身份信息载荷（Type=4）。</summary>
    public CipherIdentityRequest? Identity { get; init; }

    /// <summary>标签。</summary>
    public List<string>? Tags { get; init; }

    /// <summary>自定义字段。Id 由服务端按 0,1,2… 重新编号（复合主键要求）。</summary>
    public List<CipherFieldRequest>? Fields { get; init; }
}

/// <summary>
/// 更新条目。全量覆盖语义：未带的字段会被重置，调用方必须传完整对象。
/// <c>Id</c> 只用于定位本地记录，不参与 Bitwarden 请求体构造。
/// </summary>
public sealed record UpdateCipherRequest
{
    public string Id { get; init; } = "";
    public string? AccountId { get; init; }
    public int Type { get; init; }
    public string? Name { get; init; }
    public string? Notes { get; init; }
    public bool Favorite { get; init; }
    public string? FolderId { get; init; }
    public CipherLoginRequest? Login { get; init; }
}

/// <summary>登录类条目的载荷（其他类型为 null）。</summary>
public sealed record CipherLoginRequest
{
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Totp { get; init; }
    public List<string>? Uris { get; init; }
}

/// <summary>银行卡载荷（<see cref="CreateCipherRequest.Card"/> / 导入还原用）。</summary>
public sealed record CipherCardRequest
{
    public string? CardholderName { get; init; }
    public string? Number { get; init; }
    public string? Brand { get; init; }
    public string? ExpMonth { get; init; }
    public string? ExpYear { get; init; }
    public string? Code { get; init; }
}

/// <summary>身份信息载荷。</summary>
public sealed record CipherIdentityRequest
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Address { get; init; }
    public string? Ssn { get; init; }
    public string? Username { get; init; }
}

/// <summary>自定义字段载荷。<c>Id</c> 不在此处：由服务端按条目内 0,1,2… 编号（复合主键要求）。</summary>
public sealed record CipherFieldRequest
{
    public string Name { get; init; } = "";
    public string? Value { get; init; }
    public int Type { get; init; }
    public bool Hidden { get; init; }
}

// === 响应 ===

/// <summary>条目查询结果。三个列表都保证非 null（无结果时为空列表）。</summary>
public sealed record CipherSearchResponse(List<CipherDto> Ciphers, List<FolderDto> Folders, CipherFacets Facets);

/// <summary>
/// 分面计数：每个可选值在「其它栏目的筛选都生效、但**不含它自己这一栏**」的条件下还能命中多少条。
///
/// 为什么必须排除自己那一栏：筛选栏要"查不到就把选项灰掉"，而这个判断在选了文件夹 A 之后
/// 不能变成"只看 A，所以别的文件夹都是 0"——那样一选就锁死，切换不了也叠加不了。
/// 排除自己这一栏之后，计数回答的才是"我现在再选它，还能不能查到东西"。
/// </summary>
public sealed record CipherFacets(
    /// <summary>当前文本查询命中总数（不含任何分面筛选）</summary>
    int Total,
    /// <summary>固定 4 种类型，Count=0 的要在 UI 里灰掉</summary>
    List<TypeFacet> Types,
    /// <summary>每个文件夹 + 「未分类」，同样带可达计数</summary>
    List<FolderFacet> Folders,
    int TotpCount,
    int PasskeyCount);

public sealed record TypeFacet(int Type, int Count);

/// <summary>文件夹分面项；<c>Id</c> 为空字符串表示「未分类」。</summary>
public sealed record FolderFacet(string Id, string Name, int Count);

/// <summary>条目列表项。</summary>
public sealed record CipherDto(
    string Id,
    int Type,
    string? Name,
    string? Notes,
    bool Favorite,
    string? FolderId,
    CipherLoginDto? Login,
    DateTime CreatedDate,
    DateTime RevisionDate,
    string SyncStatus);

/// <summary>登录类条目的列表视图。</summary>
public sealed record CipherLoginDto(string? Username, string? Password, List<string>? Uris, string? Totp);

/// <summary>文件夹列表项。</summary>
public sealed record FolderDto(string Id, string Name, string SyncStatus = "synced");

/// <summary>新建条目的结果。</summary>
public sealed record CipherCreateResponse(string Id, string? Name, string SyncStatus);

/// <summary>新建文件夹的结果。</summary>
public sealed record FolderCreateResponse(string Id, string Name);

/// <summary>导出结果：落盘路径与条目数。</summary>
public sealed record VaultExportResponse(string Path, int Count);
