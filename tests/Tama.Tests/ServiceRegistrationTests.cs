using Tama.Core.Contracts;
using Tama.Core.Interfaces;
using Tama.Data.Database;
using Tama.Services;
using Tama.Services.Biometric;
using Tama.Services.Bridge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Tama.Tests;

/// <summary>
/// DI 装配回归：<see cref="ServiceCollectionExtensions.AddTamaServices"/> 里每个领域契约接口
/// 都必须能真正解析出实现。
///
/// 为什么值得单独测：接口注册是 `AddScoped&lt;IVaultApi&gt;(sp =&gt; sp.GetRequiredService&lt;CipherService&gt;())`
/// 这种"转发"写法——把 CipherService 手滑写成 FolderService 依然**能编译**，
/// 只有运行时第一次注入该页面才会炸。页面是按需解析的，正常点测根本盖不到。
/// </summary>
public class ServiceRegistrationTests
{
    /// <summary>与宿主同构的最小容器：共用注册 + 宿主负责的部分（DbContext、平台能力）用替身。</summary>
    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // 宿主负责：DbContext（这里用不开连接的 SQLite 路径，仅需能构造）
        services.AddDbContext<TamaDbContext>(o => o.UseSqlite("Data Source=:memory:"));
        services.AddDbContext<AuthDbContext>(o => o.UseSqlite("Data Source=:memory:"));

        // 宿主负责：平台能力（浏览器宿主用 Null 降级）
        services.AddSingleton<IBiometricService, NullBiometricService>();
        services.AddSingleton<IPasskeyPlatformService, NullPasskeyPlatformService>();

        services.AddTamaServices();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Every_Domain_Contract_Resolves()
    {
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        Assert.NotNull(sp.GetRequiredService<IAuthApi>());
        Assert.NotNull(sp.GetRequiredService<IVaultApi>());
        Assert.NotNull(sp.GetRequiredService<IFolderApi>());
        Assert.NotNull(sp.GetRequiredService<IWatchtowerApi>());
        Assert.NotNull(sp.GetRequiredService<IPasswordApi>());
        Assert.NotNull(sp.GetRequiredService<IPasskeyApi>());
        Assert.NotNull(sp.GetRequiredService<IWebAuthnApi>());
        Assert.NotNull(sp.GetRequiredService<IImportApi>());
        // 导入页注入的就是它；转发写错（比如指回 KeePassImportService）照样能编译，
        // 只有用户点开导入页那一刻才炸——正是本测试存在的理由。
        Assert.NotNull(sp.GetRequiredService<IVaultImportApi>());
    }

    [Fact]
    public void Extension_Bridge_Resolves()
    {
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ExtensionBridge>());
    }

    [Fact]
    public void Contract_Interface_And_Concrete_Service_Share_One_Instance()
    {
        // 页面注入 IVaultApi，其他服务注入 CipherService——必须是同一个 Scoped 实例，
        // 否则两者的 DbContext 状态会分裂。
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        Assert.Same(sp.GetRequiredService<Tama.Services.Vault.CipherService>(), sp.GetRequiredService<IVaultApi>());
        Assert.Same(sp.GetRequiredService<Tama.Services.Auth.AuthService>(), sp.GetRequiredService<IAuthApi>());
        Assert.Same(sp.GetRequiredService<Tama.Services.Import.VaultImportService>(), sp.GetRequiredService<IVaultImportApi>());
    }
}
