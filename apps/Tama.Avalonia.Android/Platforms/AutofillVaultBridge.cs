using Microsoft.Extensions.DependencyInjection;
using Tama.Core.Contracts;

using TamaApp = Tama.Avalonia.App;

namespace Tama.Avalonia.Android.Platforms;

/// <summary>
/// Autofill ↔ Tama 保险库：从宿主环境解析 DI 作用域，**强类型**直调 <see cref="IVaultApi"/>
/// （与页面同款写法，没有任何 JSON/反射）。
///
/// 两个关键决定：
///   · **自己开一个新的作用域**，不复用 UI 会话那个：领域服务是 Scoped，DbContext 不是线程安全的，
///     而 autofill 的调用来自系统的 binder 线程，和界面同时用同一个 DbContext 会炸。
///     新作用域靠单例里的数据库密钥打开同一个库（已解锁才有密钥；没解锁就是空结果，不是崩溃）。
///   · 拿不到宿主（进程还没初始化 / 已锁定）时返回 null，调用方静默跳过——
///     autofill 绝不能因为旁边的问题而弹错误对话框或阻塞用户输入。
/// </summary>
internal sealed class AutofillVaultBridge : IDisposable
{
    private readonly IVaultApi _vault;
    private readonly IServiceScope _scope;

    private AutofillVaultBridge(IVaultApi vault, IServiceScope scope)
    {
        _vault = vault;
        _scope = scope;
    }

    public static AutofillVaultBridge? Create()
    {
        var environment = TamaApp.HostEnvironment;
        if (environment == null) return null;

        var scope = environment.Host.Services.CreateScope();
        var vault = scope.ServiceProvider.GetService<IVaultApi>();
        if (vault == null)
        {
            scope.Dispose();
            return null;
        }

        return new AutofillVaultBridge(vault, scope);
    }

    public void Dispose() => _scope.Dispose();

    /// <summary>按关键字（域名 / 包名 / 用户名）搜登录类条目。</summary>
    public List<VaultCipher> SearchCiphers(string query)
    {
        try
        {
            var result = _vault.Search(new CipherSearchRequest
            {
                Types = new List<int> { 1 },
                Query = query,
            }).GetAwaiter().GetResult();

            return result.Ciphers
                .Where(c => c.Login != null)
                .Select(c => new VaultCipher(
                    c.Id,
                    c.Name ?? "",
                    c.Login!.Username ?? "",
                    c.Login.Password ?? "",
                    c.Login.Uris ?? new List<string>(),
                    c.Notes,
                    c.Favorite,
                    c.FolderId))
                .ToList();
        }
        catch
        {
            // 库锁着 / 解析失败：当作没有数据，别阻塞别人的输入框
            return new List<VaultCipher>();
        }
    }

    /// <summary>
    /// 保存或更新凭据：先按用户名找已有条目，命中就更新（合并 URI），否则新建。
    ///
    /// ⚠ 更新必须把 Name/Notes/Favorite/FolderId 一起带上：<see cref="IVaultApi.Update"/> 是
    /// 整条覆盖语义，少传等于把它们重置成空/未收藏——用户只是在一个网页里存了个密码，
    /// 结果条目名和文件夹没了。卡片/身份/自定义字段不在这条链路上（它们由服务层从本地实体
    /// 重新构造请求体，不会因为 DTO 缺字段而被清空）。
    /// </summary>
    public void SaveOrUpdate(string username, string password, string searchKey)
    {
        var existing = SearchCiphers(username).FirstOrDefault(c =>
            string.Equals(c.Username, username, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            var uris = existing.Uris;
            if (!string.IsNullOrEmpty(searchKey) &&
                !uris.Any(u => string.Equals(u, searchKey, StringComparison.OrdinalIgnoreCase)))
                uris.Add(searchKey);

            _vault.Update(new UpdateCipherRequest
            {
                Id = existing.Id,
                Type = 1,
                Name = existing.Name,
                Notes = existing.Notes,
                Favorite = existing.Favorite,
                FolderId = existing.FolderId,
                Login = new CipherLoginRequest { Username = username, Password = password, Uris = uris },
            }).GetAwaiter().GetResult();
        }
        else
        {
            _vault.Create(new CreateCipherRequest
            {
                Type = 1,
                Name = string.IsNullOrEmpty(searchKey) ? username : searchKey,
                Login = new CipherLoginRequest
                {
                    Username = username,
                    Password = password,
                    Uris = !string.IsNullOrEmpty(searchKey) ? new List<string> { searchKey } : null,
                },
            }).GetAwaiter().GetResult();
        }
    }
}

/// <summary>autofill 需要的那部分条目字段。</summary>
internal sealed record VaultCipher(
    string Id,
    string Name,
    string Username,
    string Password,
    List<string> Uris,
    string? Notes,
    bool Favorite,
    string? FolderId);
