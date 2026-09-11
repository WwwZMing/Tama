using Microsoft.Data.Sqlite;

namespace Tama.Data.Database;

/// <summary>
/// 数据库加密迁移工具：将已有明文 SQLite 数据库原地升级为加密数据库。
/// 使用 SQLite3 Multiple Ciphers (sqleet: ChaCha20-Poly1305) 作为默认加密方案。
/// </summary>
public static class DatabaseEncryptionHelper
{
    /// <summary>
    /// 尝试用指定密码打开数据库，成功返回 true。
    /// 用于验证主密码是否正确（主密码直接派生为 DB 加密密钥）。
    /// </summary>
    public static bool TryOpen(string dbPath, string password)
    {
        if (!File.Exists(dbPath))
            return false;

        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Password = password
            };
            using var connection = new SqliteConnection(csb.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master";
            command.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EncryptDatabase(string dbPath, string password)
    {
        // 用明文打开
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite");
        connection.Open();

        // 使用 quote() 安全转义密码
        using var quoteCmd = connection.CreateCommand();
        quoteCmd.CommandText = "SELECT quote($pwd)";
        quoteCmd.Parameters.AddWithValue("$pwd", password);
        var quotedPassword = (string)quoteCmd.ExecuteScalar()!;

        // 原地加密（默认 sqleet: ChaCha20-Poly1305）
        using var rekeyCmd = connection.CreateCommand();
        rekeyCmd.CommandText = $"PRAGMA rekey = {quotedPassword}";
        rekeyCmd.ExecuteNonQuery();
    }
}
