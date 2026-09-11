using Tama.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;

namespace Tama;

/// <summary>
/// Android Autofill ↔ Tama 保险库：解析 DI scope 后**强类型**直调 <see cref="IVaultApi"/>。
///
/// 重构前这里手工拼 JSON 字符串再用 JsonDocument 逐字段解析（走已删除的 HandleMessage 通道），
/// 与 UI 页面是两套写法。现在与页面完全同款：构造请求对象 → await IVaultApi → 读 CipherDto。
/// </summary>
internal class AutofillVaultBridge : IDisposable
{
    private readonly IVaultApi _vault;
    private readonly IServiceScope _scope;

    /// <summary>从 MAUI 服务容器解析（失败返回 null，Autofill 调用方静默跳过）。</summary>
    public static AutofillVaultBridge? Create()
    {
        var platformApp = IPlatformApplication.Current;
        if (platformApp == null) return null;
        var scopeFactory = platformApp.Services.GetService<IServiceScopeFactory>();
        if (scopeFactory == null) return null;
        var scope = scopeFactory.CreateScope();
        var vault = scope.ServiceProvider.GetService<IVaultApi>();
        return vault == null ? null : new AutofillVaultBridge(vault, scope);
    }

    private AutofillVaultBridge(IVaultApi vault, IServiceScope scope)
    {
        _vault = vault;
        _scope = scope;
    }

    public void Dispose() => _scope.Dispose();

    /// <summary>搜索条目（query 按业务传：填充传 searchKey，保存传 username——与历史行为一致）。</summary>
    public List<VaultCipher> SearchCiphers(string query)
    {
        try
        {
            var result = _vault.Search(new CipherSearchRequest { Types = new List<int> { 1 }, Query = query })
                .GetAwaiter().GetResult();

            return result.Ciphers
                .Where(c => c.Login != null)
                .Select(c => new VaultCipher(
                    c.Id,
                    c.Login!.Username ?? "",
                    c.Login.Password ?? "",
                    c.Login.Uris ?? new List<string>()))
                .ToList();
        }
        catch
        {
            // autofill 静默失败，不阻塞用户
            return new List<VaultCipher>();
        }
    }

    /// <summary>保存或更新凭据：先按用户名搜索已有凭据，命中则更新（追加 URI），否则创建。</summary>
    public void SaveOrUpdate(string username, string password, string searchKey)
    {
        // 与历史行为一致：按用户名搜（而非 searchKey）
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

/// <summary>cipher/search 的结构化结果。</summary>
internal record VaultCipher(string Id, string Username, string Password, List<string> Uris);
