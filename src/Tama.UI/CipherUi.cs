using Tama.Core.Contracts;

namespace Tama.UI;

/// <summary>
/// Cipher 相关的展示辅助（跨 Vault 列表 / 详情 / 编辑页共用）。
/// Razor 隐式表达式里不能写带引号的方法调用（如 @x.ToString("fmt") 会截断），
/// 时间格式化等一律走这里的静态方法。
/// </summary>
public static class CipherUi
{
    public static string IconName(int type) => type switch
    {
        1 => "key",
        2 => "note",
        3 => "card",
        4 => "id",
        _ => "key",
    };

    public static string TypeLabel(int type) => type switch
    {
        1 => "登录",
        2 => "安全笔记",
        3 => "银行卡",
        4 => "身份信息",
        _ => "条目",
    };

    /// <summary>
    /// 同步状态徽章配色：三档而不是两档。
    /// 旧写法只分 synced/其它，pending（排队中）和 failed（推了很多次没成功）一个颜色，
    /// 用户根本看不出"这条其实一直没同步上去"——而那种条目正处在会被拉取覆盖的风险里。
    /// </summary>
    public static string SyncBadgeClass(string status) => status switch
    {
        "synced" => "badge-ok",
        "failed" => "badge-no",
        _ => "badge-warn",
    };

    public static string FavTitle(bool fav) => fav ? "取消收藏" : "收藏";

    public static string FmtDate(DateTime dt) => dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public static string FolderName(string? folderId, IEnumerable<FolderDto>? folders)
    {
        if (string.IsNullOrEmpty(folderId)) return "未分类";
        return folders?.FirstOrDefault(x => x.Id == folderId)?.Name ?? "未分类";
    }
}
