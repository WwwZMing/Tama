using Tama.Core;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Tama.Core.Interfaces;
using Tama.Data.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Tama.Services.Auth;

public class AuthSessionService : IAuthSession
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVaultCrypto _crypto;
    private readonly DatabaseKeyService _dbKeyService;
    private bool _dbInitialized;
    private DateTime? _lastMasterPasswordVerifiedAt;
    private static readonly TimeSpan MasterPasswordReverifyInterval = TimeSpan.FromHours(72);
    private static readonly ILogger Log = Serilog.Log.ForContext<AuthSessionService>();

    public bool IsSetup { get; private set; }
    public bool IsUnlocked => _dbKeyService.IsUnlocked;
    public string? Password { get; private set; }

    /// <summary>距上次主密码验证（setup/密码解锁）是否已超过 72 小时——超时后指纹解锁须先验证主密码。</summary>
    public bool RequireMasterPasswordRecheck()
        => _lastMasterPasswordVerifiedAt == null
           || DateTime.UtcNow - _lastMasterPasswordVerifiedAt.Value > MasterPasswordReverifyInterval;

    private void LoadSessionState()
    {
        try
        {
            if (!File.Exists(AppPaths.SessionPath)) return;
            var json = File.ReadAllText(AppPaths.SessionPath);
            // 写入端用匿名对象（camelCase 小写键 lastMasterPasswordVerifiedAt），读回必须大小写不敏感，
            // 否则反序列化静默得 null → 72h 计时丢失 → 重启后指纹解锁按钮永远不出现
            var state = JsonSerializer.Deserialize<SessionStateData>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (state?.LastMasterPasswordVerifiedAt != null)
                _lastMasterPasswordVerifiedAt = state.LastMasterPasswordVerifiedAt;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load session state, ignoring");
        }
    }

    private void SaveSessionState()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(AppPaths.SessionPath,
                JsonSerializer.Serialize(new { lastMasterPasswordVerifiedAt = _lastMasterPasswordVerifiedAt?.ToString("O") }));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save session state");
        }
    }

    private class SessionStateData
    {
        public DateTime? LastMasterPasswordVerifiedAt { get; set; }
    }

    public AuthSessionService(IServiceScopeFactory scopeFactory, IVaultCrypto crypto,
        DatabaseKeyService dbKeyService)
    {
        _scopeFactory = scopeFactory;
        _crypto = crypto;
        _dbKeyService = dbKeyService;

        // 路径全部来自 AppPaths（唯一来源）。这里**刻意不建目录**：目录由宿主启动时调
        // AppPaths.Initialize() 建好，而那一步必须先完成旧目录迁移。
        // 早先在这里"防御性"建目录，实测把迁移堵死了——迁移只在旧目录存在、新目录不存在时
        // 才动手，构造函数先造出一个空的新目录 → 迁移判定"两边都在"而撒手，用户的库搬不过来。
        // 写文件的地方（SaveSessionState / SetupAsync）各自保证目录存在。
        LoadSessionState();
    }

    public Task<bool> IsSetupAsync()
    {
        IsSetup = File.Exists(AppPaths.FingerprintPath);
        return Task.FromResult(IsSetup);
    }

    public async Task SetupAsync(string password)
    {
        var salt = _crypto.GenerateSalt();
        var kdfIterations = 100_000;

        // 从主密码派生 DB 加密密钥
        var key = _crypto.DeriveKey(password, salt, kdfIterations);
        _dbKeyService.SetKey(key);

        // 保存指纹（salt 用于后续解锁时派生相同的 key）
        var fingerprint = new { salt = Convert.ToBase64String(salt), kdfIterations };
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(AppPaths.FingerprintPath, JsonSerializer.Serialize(fingerprint));

        Password = password;
        IsSetup = true;
        _lastMasterPasswordVerifiedAt = DateTime.UtcNow; // setup 已输入主密码，72h 计时开始
        SaveSessionState();

        Log.Information("Vault setup complete, fingerprint saved to {Path}", AppPaths.FingerprintPath);

        // DB 现在可以用密钥创建/打开
        await EnsureDbInitializedAsync();
    }

    public async Task<bool> UnlockAsync(string password)
    {
        if (!File.Exists(AppPaths.FingerprintPath))
            return false;

        // 打点：解锁手感=这一段的总时长。实测 PBKDF2(100k) 仅 ~15ms，
        // 大头在 TryOpen（SQLite3MC 密钥派生）与首次触库的 EF 模型构建。
        var sw = Stopwatch.StartNew();

        var fingerprintJson = File.ReadAllText(AppPaths.FingerprintPath);
        // 写入端用匿名对象序列化（小写键 salt/kdfIterations），读回必须大小写不敏感，
        // 否则 Salt 匹配不上会变空串 → 派生密钥错误 → 解锁永远失败
        var fingerprint = JsonSerializer.Deserialize<FingerprintData>(fingerprintJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (fingerprint == null) return false;

        var salt = Convert.FromBase64String(fingerprint.Salt);
        var kdfIterations = fingerprint.KdfIterations > 0 ? fingerprint.KdfIterations : 100_000;

        // 从密码派生 DB 密钥
        var key = _crypto.DeriveKey(password, salt, kdfIterations);
        var tDerive = sw.ElapsedMilliseconds;
        var dbPassword = Convert.ToHexString(key);

        // 验证：尝试用派生密钥打开数据库
        var vaultOk = DatabaseEncryptionHelper.TryOpen(AppPaths.VaultDbPath, dbPassword);
        var tVaultOpen = sw.ElapsedMilliseconds - tDerive;
        if (!vaultOk)
        {
            // 也试试 authDb（兼容只有一个 DB 的场景）
            if (!DatabaseEncryptionHelper.TryOpen(AppPaths.AuthDbPath, dbPassword))
                return false;
        }
        var tOpen = sw.ElapsedMilliseconds - tDerive;

        _dbKeyService.SetKey(key);
        Password = password;
        _lastMasterPasswordVerifiedAt = DateTime.UtcNow; // 用真密码解锁成功，刷新 72h 计时
        SaveSessionState();

        // DB 可能尚未初始化（首次启动）
        await EnsureDbInitializedAsync();
        var tInit = sw.ElapsedMilliseconds - tDerive - tOpen;

        Log.Information(
            "Unlock timing: derive={Derive}ms tryOpen={Open}ms (vault={Vault}ms vaultOk={VaultOk}) dbInit={Init}ms total={Total}ms",
            tDerive, tOpen, tVaultOpen, vaultOk, tInit, sw.ElapsedMilliseconds);

        return true;
    }

    public void Lock()
    {
        Password = null;
        _dbKeyService.Lock();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        // 注意：不重置 _lastMasterPasswordVerifiedAt——锁定后指纹解锁仍受 72h 策略约束
    }

    /// <summary>
    /// 确保数据库已创建。仅执行一次。
    ///
    /// 这里**不再写任何示例数据**（原来会塞 3 个文件夹 + 十几条假条目）：
    /// 空的保险库就是空的，用户第一次看到的应该是自己的东西，不是一串"Google Account / GitHub"演示项。
    /// 那些假数据还会在关联 Bitwarden 之后被当成"服务器上没有的本地条目"（或被 worker 推到云端），
    /// 是纯负担。历史库里的旧示例数据不会自动清，需要用户自己删（它们多半是 pending 状态）。
    /// </summary>
    private async Task EnsureDbInitializedAsync()
    {
        if (_dbInitialized) return;
        _dbInitialized = true;

        var sw = Stopwatch.StartNew();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tScope = sw.ElapsedMilliseconds;
            var vaultDb = scope.ServiceProvider.GetRequiredService<TamaDbContext>();
            await vaultDb.Database.EnsureCreatedAsync();
            var tEnsureVault = sw.ElapsedMilliseconds - tScope;

            var authDb = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await authDb.Database.EnsureCreatedAsync();
            var tEnsureAuth = sw.ElapsedMilliseconds - tScope - tEnsureVault;

            Log.Information("Database initialized (scope={Scope}ms ensureVault={Vault}ms ensureAuth={Auth}ms total={Total}ms)",
                tScope, tEnsureVault, tEnsureAuth, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _dbInitialized = false; // 重试
            Log.Error(ex, "Failed to initialize database");
            throw;
        }
    }

    private class FingerprintData
    {
        public string Salt { get; set; } = "";
        public int KdfIterations { get; set; } = 100_000;
    }
}
