namespace Tama.Core.Models;

public class VaultFingerprint
{
    public string Id { get; set; } = "default";
    public int KdfVersion { get; set; } = 1;
    public byte[] Salt { get; set; } = Array.Empty<byte>();
    public byte[] Hash { get; set; } = Array.Empty<byte>();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
