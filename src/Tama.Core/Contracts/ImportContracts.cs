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
