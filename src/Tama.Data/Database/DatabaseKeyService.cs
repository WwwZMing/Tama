using System.Security.Cryptography;

namespace Tama.Data.Database;

/// <summary>
/// 内存中的数据库加密密钥管理器。
/// 密钥由主密码通过 PBKDF2 派生，不持久化到磁盘。
/// </summary>
public class DatabaseKeyService
{
    private byte[]? _databaseKey;

    /// <summary>密钥是否已解锁（主密码已验证）</summary>
    public bool IsUnlocked => _databaseKey != null;

    /// <summary>设置派生后的 DB 密钥（由 AuthSessionService 在验证密码后调用）</summary>
    public void SetKey(byte[] key)
    {
        _databaseKey = key;
    }

    /// <summary>获取当前密钥，未解锁时抛出异常</summary>
    public byte[] GetKey()
    {
        if (_databaseKey == null)
            throw new InvalidOperationException("Vault is locked. Unlock first.");
        return _databaseKey;
    }

    /// <summary>获取密钥的 hex 字符串（用于 SQLite 密码）</summary>
    public string GetDbPassword()
    {
        return Convert.ToHexString(GetKey());
    }

    /// <summary>清除内存中的密钥</summary>
    public void Lock()
    {
        if (_databaseKey != null)
        {
            CryptographicOperations.ZeroMemory(_databaseKey);
            _databaseKey = null;
        }
    }
}
