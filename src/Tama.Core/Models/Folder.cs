namespace Tama.Core.Models;

public class Folder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // === 离线写入队列（与 Cipher 同构，见 Cipher 上那三列的注释）===
    public string SyncStatus { get; set; } = "synced";
    public string? PendingOp { get; set; }
    public int RetryCount { get; set; } = 0;
    public DateTime? LastAttempt { get; set; }

    /// <summary>
    /// 已经删掉了、只等推给服务器。**列表类查询必须把它滤掉**，否则用户删完文件夹
    /// 它还在筛选项里杵着（本地行要等推送成功才真删，不能删早了——删早了这个删除就丢了）。
    /// </summary>
    public bool IsPendingDelete => SyncStatus == "pending" && PendingOp == "delete";

    /// <summary>
    /// 深拷贝成一条**新键**的条目，理由与 <see cref="Cipher.DeepCopyWithId"/> 完全相同：
    /// EF 不允许修改已跟踪实体的键属性，而离线创建成功后要把本地 Guid 换成服务器 ID
    /// （唯一干净的做法是"删旧行 + 以新键插入新行"）。
    /// </summary>
    public Folder DeepCopyWithId(Guid newId) => new()
    {
        Id = newId,
        Name = Name,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        SyncStatus = "synced",
        PendingOp = null,
        RetryCount = 0,
        LastAttempt = null,
    };
}
