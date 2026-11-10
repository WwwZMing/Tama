namespace Tama.Core.Contracts;

/// <summary>
/// KeePass (.kdbx) 导入。
/// 实现：<c>Tama.Services.Import.KeePassImportService</c>。
///
/// 文件以 <c>byte[]</c> 直接传入——早先的契约是 base64 字符串（JSON 通道的产物），
/// 进程内已无必要，也省掉一次编解码。
/// </summary>
public interface IImportApi
{
    /// <summary>解析顶层分组摘要（名称 + 条目数），供用户选择导入范围。纯解析，不落库。</summary>
    List<KdbxGroupDto> ParseGroups(byte[] file, string? password = null);

    /// <summary>
    /// 导入条目（可只导入某个顶层分组，null 为全部），返回导入条数。
    /// 逐条走 CipherService 的乐观写入链路：本地落库 + 有账号则推送，失败留 pending 重试。
    /// </summary>
    Task<int> Import(byte[] file, string? password = null, string? group = null);
}

/// <summary>导入预览用的分组项。</summary>
public sealed record KdbxGroupDto(string Name, int EntryCount);

/// <summary>
/// Tama 自家 JSON 备份（<c>tama-json</c>）的还原——导出的对侧。
/// 实现：<c>Tama.Services.Import.VaultImportService</c>。
///
/// 与 <see cref="IImportApi"/>（KeePass）分开：那一个吃 KDBX 且只产 Login，
/// 这一个吃自家格式、要还原四种类型 + 标签 + 自定义字段 + 文件夹。
/// </summary>
public interface IVaultImportApi
{
    /// <summary>
    /// 只读预览：识别格式并统计各类型条数与文件夹名，**不落库、不抛异常**
    /// （格式不对/JSON 坏掉时返回 <see cref="VaultImportPreview.Error"/>，由 UI 展示）。
    /// </summary>
    VaultImportPreview Preview(byte[] file);

    /// <summary>
    /// 执行导入。逐条走 <c>CipherService.Create</c> 的乐观写入链路
    /// （本地加密落库 + 有账号则推送，失败留 pending 由 SyncWorker 重试）。
    /// </summary>
    Task<VaultImportResponse> Import(byte[] file, VaultImportOptions? options = null);
}

public sealed record VaultImportOptions
{
    /// <summary>
    /// 按备份里的文件夹名还原文件夹；已存在同名文件夹则复用（不重复建）。
    /// 默认 true。关掉则所有条目都进「未分类」。
    /// </summary>
    public bool RestoreFolders { get; init; } = true;

    /// <summary>
    /// 跳过「同类型 + 同名称 + 同用户名」的已存在条目。默认 true。
    ///
    /// 为什么默认开：导出格式里**没有条目 Id**（备份要能跨库还原），所以同一份文件导两次
    /// 默认会产生两整套副本。这是密码管理器里最容易让人后悔的操作之一。
    /// </summary>
    public bool SkipDuplicates { get; init; } = true;
}

/// <summary>导入预览结果。<see cref="IsTamaVault"/> 为 false 时只有 <see cref="Error"/> 有意义。</summary>
public sealed record VaultImportPreview(
    bool IsTamaVault,
    string? Format,
    int Version,
    string? Error,
    int Total,
    IReadOnlyList<VaultImportTypeCount> Types,
    IReadOnlyList<string> Folders,
    DateTime? ExportedAt)
{
    /// <summary>文件不是自家备份（例如用户选错了文件）。</summary>
    public static VaultImportPreview NotTamaVault(string error) =>
        new(false, null, 0, error, 0, Array.Empty<VaultImportTypeCount>(), Array.Empty<string>(), null);
}

/// <summary>预览里按类型统计的一项。<c>Type</c> 用中文标签（"登录"/"银行卡"…）便于直接展示。</summary>
public sealed record VaultImportTypeCount(string Type, int Count);

/// <summary>
/// 导入结果。<c>Imported</c> + <c>Skipped</c> 等于备份里的条目总数——
/// <c>Skipped</c> 里既有判定为重复的，也有格式坏掉被跳过的，后者在 <see cref="Warnings"/> 里逐条说明。
/// </summary>
public sealed record VaultImportResponse(
    int Imported,
    int Skipped,
    int FoldersCreated,
    IReadOnlyList<string> Warnings);
