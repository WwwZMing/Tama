namespace Tama.Core.Models;

public class SyncCache
{
    public string Id { get; set; } = "default";
    public string AccountId { get; set; } = string.Empty;
    public string CiphersJson { get; set; } = "[]";
    public string FoldersJson { get; set; } = "[]";
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
}
