using System.Security.Cryptography;

namespace Tama.Data.Database;

/// <summary>
/// 内存中的数据库加密密钥管理器。
/// 密钥由主密码通过 PBKDF2 派生，不持久化到磁盘。
/// </summary>
public class DatabaseKeyService
{
    private byte[]? _databaseKey;
    private readonly object _gate = new();

    /// <summary>密钥是否已解锁（主密码已验证）</summary>
    public bool IsUnlocked => Volatile.Read(ref _databaseKey) != null;

    /// <summary>
    /// 设置派生后的 DB 密钥（由 AuthSessionService 在验证密码后调用）。
    /// 服务自己持有一份副本，并立即清零被替换的旧密钥；调用方仍应清掉传入数组。
    /// </summary>
    public void SetKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("Database key must be 32 bytes", nameof(key));

        var replacement = (byte[])key.Clone();
        byte[]? previous;
        lock (_gate)
        {
            previous = _databaseKey;
            Volatile.Write(ref _databaseKey, replacement);
        }
        if (previous is not null)
            CryptographicOperations.ZeroMemory(previous);
    }

    /// <summary>获取当前密钥，未解锁时抛出异常</summary>
    public byte[] GetKey()
    {
        var key = Volatile.Read(ref _databaseKey);
        if (key == null)
            throw new InvalidOperationException("Vault is locked. Unlock first.");
        return key;
    }

    /// <summary>
    /// 取一份可安全交给密钥包装逻辑的副本。调用方负责在完成后清零。
    /// 不要用 <see cref="GetKey"/> 的返回数组长期持有，锁定时那个数组会被原地清零。
    /// </summary>
    public byte[] CopyKey()
    {
        var key = Volatile.Read(ref _databaseKey);
        if (key == null)
            throw new InvalidOperationException("Vault is locked. Unlock first.");
        return (byte[])key.Clone();
    }

    /// <summary>获取密钥的 hex 字符串（用于 SQLite 密码）</summary>
    public string GetDbPassword()
    {
        return Convert.ToHexString(GetKey());
    }

    /// <summary>清除内存中的密钥</summary>
    public void Lock()
    {
        lock (_gate)
        {
            var key = Volatile.Read(ref _databaseKey);
            Volatile.Write(ref _databaseKey, null);
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
        }
    }
}
