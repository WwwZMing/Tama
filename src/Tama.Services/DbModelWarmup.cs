using Tama.Data.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Tama.Services;

/// <summary>
/// 启动期预热 EF 模型。
///
/// 背景（实测）：解锁主密码库要 ~1s，而 PBKDF2(100k) 只占 ~15ms、SQLite3MC 开库占
/// ~200-300ms，剩下数百毫秒是 <b>EF Core 惰性构建 IModel</b>——本项目模型含表拆分
/// （Cipher/CipherCard/CipherIdentity/CipherNote）、owned type 与值转换器，首次触库
/// 才构建，而"首次触库"恰好发生在解锁时（解锁前 vault DbContext 从没真正查询过）。
/// 于是模型构建的开销被算进了 auth/unlock，表现为"点解锁卡一下"。
///
/// 预热只触 <c>DbContext.Model</c>（不开连接、不需要 DB 密钥），因此锁屏状态下也能安全执行。
/// </summary>
public static class DbModelWarmup
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(DbModelWarmup));

    /// <summary>同步预热两个 DbContext 的模型。应在后台线程调用，别挡启动。</summary>
    public static void WarmUp(IServiceProvider services)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var scope = services.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<TamaDbContext>().Model;
            var vault = sw.ElapsedMilliseconds;
            _ = scope.ServiceProvider.GetRequiredService<AuthDbContext>().Model;
            Log.Information("EF model warm-up done (vault={Vault}ms total={Total}ms)", vault, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // 预热失败不影响正确性：只是把开销退回给首次解锁
            Log.Warning(ex, "EF model warm-up failed; first unlock will pay the cost");
        }
    }
}
