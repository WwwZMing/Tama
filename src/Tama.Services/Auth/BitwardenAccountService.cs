using Tama.Core.Interfaces;
using Tama.Data.Database;
using Tama.Services.Bitwarden;
using Microsoft.EntityFrameworkCore;

namespace Tama.Services.Auth;

/// <summary>
/// Bitwarden 账号密钥解析：从 Account 表读取解密后的 enc/mac 密钥与 token。
/// Cipher / Folder 等需要调用 Bitwarden API 的服务共用。
/// </summary>
public class BitwardenAccountService
{
    private readonly AuthDbContext _db;
    private readonly DatabaseKeyService _dbKeyService;
    private readonly IBitwardenApiClient _bitwarden;

    public BitwardenAccountService(AuthDbContext db, DatabaseKeyService dbKeyService, IBitwardenApiClient bitwarden)
    {
        _db = db;
        _dbKeyService = dbKeyService;
        _bitwarden = bitwarden;
    }

    public async Task<(string accountId, string accessToken, byte[] encKey, byte[] macKey)?> FindBitwardenAccount(string? requestedAccountId)
    {
        // ⚠ 必须 AsNoTracking。DecryptSensitiveFields 是**就地**把字段换成明文的，而被跟踪的实体
        //   只要遇上同一个 DbContext 上的任意一次 SaveChanges 就会被**明文写回库**——
        //   这个 DbContext 是 Scoped 的，调用方（AuthRefresh、FolderService 的 SyncCache 更新…）
        //   紧接着就会 SaveChanges，于是"同步一次 = 把 access token 与派生密钥明文落盘"。
        //   本方法只负责把密钥读出来，调用方不需要跟踪实体。
        var account = requestedAccountId != null
            ? await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == requestedAccountId)
            : await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Type == "bitwarden" && !string.IsNullOrEmpty(a.DerivedEncKey));

        if (account == null || string.IsNullOrEmpty(account.DerivedEncKey) || string.IsNullOrEmpty(account.DerivedMacKey))
            return null;

        account.DecryptSensitiveFields(_dbKeyService.GetKey());
        var encKey = Convert.FromBase64String(account.DerivedEncKey);
        var macKey = Convert.FromBase64String(account.DerivedMacKey);
        return (account.Id, account.AccessToken, encKey, macKey);
    }

    public async Task<string?> TryRefreshToken(string accountId)
    {
        try
        {
            var account = await _db.Accounts.FindAsync(accountId);
            if (account == null || string.IsNullOrEmpty(account.RefreshToken)) return null;

            // 库里的字段是密文（"enc:…"），而本方法要把新 token 写回去 → 自己解密、写完再加密。
            // 以前这里不解密，能跑通纯属**巧合**：FindBitwardenAccount 先把同一个被跟踪实例解成了
            // 明文，这里才拿到明文 refresh token。那也正是"明文落库"的来源，现在两边都修掉了。
            var key = _dbKeyService.GetKey();
            account.DecryptSensitiveFields(key);
            var result = await _bitwarden.RefreshTokenAsync(account.RefreshToken, account.AccessToken);
            if (result == null) return null;

            account.AccessToken = result.AccessToken;
            account.RefreshToken = result.RefreshToken;
            account.EncryptSensitiveFields(key);   // 存回去之前必须重新加密
            await _db.SaveChangesAsync();

            Console.WriteLine($"[Tama] Token refreshed for {account.Email}");
            return result.AccessToken;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tama] Token refresh failed: {ex.Message}");
            return null;
        }
    }
}
