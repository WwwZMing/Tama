using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Tama.Data.Database;

/// <summary>
/// 在连接打开时注入当前 DB 密钥作为 SQLite 密码（sqleet 加密）。
///
/// 为什么需要它：`AddDbContext` 把 DbContextOptions 注册为 Singleton，工厂 lambda 在
/// **服务图构造时**（首次解析 AuthService → AuthDbContext）就执行，
/// 此时用户尚未 setup/unlock，GetDbPassword() 直接抛 "Vault is locked. Unlock first."，
/// 导致所有 bridge 调用在进入业务代码前全部失败——应用整体不可用。
///
/// 修复：连接串不再携带 Password，密码改为在 **连接打开时**（ConnectionOpening）从
/// DatabaseKeyService 取当前密钥注入。解锁后访问数据库照常；未解锁时访问数据库仍抛
/// 同样的异常（语义正确），但 auth/status、auth/setup 等服务构造不再被阻断。
/// </summary>
public sealed class VaultDbConnectionInterceptor : DbConnectionInterceptor
{
    private readonly DatabaseKeyService _keyService;

    public VaultDbConnectionInterceptor(DatabaseKeyService keyService)
    {
        _keyService = keyService;
    }

    public override InterceptionResult ConnectionOpening(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        ApplyPassword(connection);
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        ApplyPassword(connection);
        return new ValueTask<InterceptionResult>(result);
    }

    private void ApplyPassword(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite) return;

        if (!_keyService.IsUnlocked)
            throw new InvalidOperationException("Vault is locked. Unlock first.");

        var builder = new SqliteConnectionStringBuilder(sqlite.ConnectionString)
        {
            Password = _keyService.GetDbPassword(),
        };
        sqlite.ConnectionString = builder.ToString();
    }
}
