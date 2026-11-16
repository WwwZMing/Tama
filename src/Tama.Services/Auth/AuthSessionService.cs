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
    private readonly VaultAccessStore _accessStore;
    private readonly IBiometricService _bio;
    private bool _dbInitialized;
    private DateTime? _lastMasterPasswordVerifiedAt;
    private static readonly TimeSpan MasterPasswordReverifyInterval = TimeSpan.FromHours(72);
    private static readonly ILogger Log = Serilog.Log.ForContext<AuthSessionService>();

    public bool IsSetup { get; private set; }
    public bool IsUnlocked => _dbKeyService.IsUnlocked;
    public string? Password { get; private set; }
    public bool HasModernDeviceCredential
    {
        get
        {
            try
            {
                return _bio.GetEncryptedPassword()?.StartsWith("v2:", StringComparison.Ordinal) == true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>距上次主密码验证（setup/密码解锁）是否已超过 72 小时——超时后指纹解锁须先验证主密码。</summary>
    public bool RequireMasterPasswordRecheck()
        // v2 设备凭据包装的是随机保险库密钥，不再缓存主密码，因此可以作为独立认证因子；
        // 旧版凭据仍遵守原来的 72 小时主密码复核策略，直到用户用密码解锁一次完成迁移。
        => !HasModernDeviceCredential
           && (_lastMasterPasswordVerifiedAt == null
               || DateTime.UtcNow - _lastMasterPasswordVerifiedAt.Value > MasterPasswordReverifyInterval);

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
        DatabaseKeyService dbKeyService, VaultAccessStore accessStore, IBiometricService bio)
    {
        _scopeFactory = scopeFactory;
        _crypto = crypto;
        _dbKeyService = dbKeyService;
        _accessStore = accessStore;
        _bio = bio;

        // 路径全部来自 AppPaths（唯一来源）。这里**刻意不建目录**：目录由宿主启动时调
        // AppPaths.Initialize() 建好，而那一步必须先完成旧目录迁移。
        // 早先在这里"防御性"建目录，实测把迁移堵死了——迁移只在旧目录存在、新目录不存在时
        // 才动手，构造函数先造出一个空的新目录 → 迁移判定"两边都在"而撒手，用户的库搬不过来。
        // 写文件的地方（SaveSessionState / SetupAsync）各自保证目录存在。
        LoadSessionState();
    }

    public Task<bool> IsSetupAsync()
    {
        IsSetup = _accessStore.Exists || File.Exists(AppPaths.FingerprintPath);
        return Task.FromResult(IsSetup);
    }

    public async Task SetupAsync(string password)
    {
        var (access, key) = _accessStore.CreateForNewVault(password);
        try
        {
            _dbKeyService.SetKey(key);

            // 保留 fingerprint.json 作为“这是旧安装可迁移的保险库”的兼容标记；
            // 实际解锁已经以 vault-access.json 为准。这里不再把密码派生值直接当数据库密钥。
            var salt = _crypto.GenerateSalt();
            var fingerprint = new { format = "wrapped-v1", salt = Convert.ToBase64String(salt), kdfIterations = 310_000 };
            Directory.CreateDirectory(AppPaths.DataDir);
            _accessStore.Save(access);
            File.WriteAllText(AppPaths.FingerprintPath, JsonSerializer.Serialize(fingerprint));

            Password = password;
            IsSetup = true;
            _lastMasterPasswordVerifiedAt = DateTime.UtcNow; // setup 已输入主密码，72h 计时开始
            SaveSessionState();
            TryAutoEnrollDeviceCredential(key);

            Log.Information("Vault setup complete, wrapped access saved to {Path}", AppPaths.VaultAccessPath);

            // DB 现在可以用密钥创建/打开
            await EnsureDbInitializedAsync();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<bool> UnlockAsync(string password)
    {
        if (!_accessStore.Exists && !File.Exists(AppPaths.FingerprintPath))
            return false;

        // 打点：解锁手感=这一段的总时长。实测 PBKDF2(100k) 仅 ~15ms，
        // 大头在 TryOpen（SQLite3MC 密钥派生）与首次触库的 EF 模型构建。
        var sw = Stopwatch.StartNew();

        byte[] key = [];
        var keyInstalled = false;
        try
        {
            var access = _accessStore.Load();
            if (access is not null)
            {
                if (!_accessStore.TryUnwrapPassword(access, password, out key))
                    return false;
            }
            else
            {
                // 旧版库：密码派生值就是数据库密钥。验证成功后只新增包装文件，数据库保持原钥匙。
                if (!TryDeriveLegacyKey(password, out key))
                    return false;
                access = _accessStore.CreateFromLegacyKey(password, key);
                _accessStore.Save(access);
                Log.Information("Migrated legacy password-derived vault key to wrapped access metadata");
            }

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
            keyInstalled = true;
            Password = password;
            _lastMasterPasswordVerifiedAt = DateTime.UtcNow; // 用真密码解锁成功，刷新 72h 计时
            SaveSessionState();
            TryAutoEnrollDeviceCredential(key);

            // DB 可能尚未初始化（首次启动）
            await EnsureDbInitializedAsync();
            var tInit = sw.ElapsedMilliseconds - tDerive - tOpen;

            Log.Information(
                "Unlock timing: derive={Derive}ms tryOpen={Open}ms (vault={Vault}ms vaultOk={VaultOk}) dbInit={Init}ms total={Total}ms",
                tDerive, tOpen, tVaultOpen, vaultOk, tInit, sw.ElapsedMilliseconds);

            return true;
        }
        catch
        {
            if (keyInstalled) _dbKeyService.Lock();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task ApplyVaultKeyAsync(byte[] vaultKey, string? newPassword = null)
    {
        if (vaultKey is null || vaultKey.Length != 32)
            throw new CryptographicException("保险库密钥长度无效");

        var previousKey = _dbKeyService.IsUnlocked ? _dbKeyService.CopyKey() : null;
        var keyInstalled = false;
        try
        {
            var dbPassword = Convert.ToHexString(vaultKey);
            var vaultOk = DatabaseEncryptionHelper.TryOpen(AppPaths.VaultDbPath, dbPassword);
            if (!vaultOk && !DatabaseEncryptionHelper.TryOpen(AppPaths.AuthDbPath, dbPassword))
                throw new CryptographicException("恢复出的密钥无法打开当前数据库");

            var access = _accessStore.Load()
                         ?? throw new InvalidOperationException("保险库访问元数据不存在");
            _dbKeyService.SetKey(vaultKey);
            keyInstalled = true;

            // 先把数据库完整初始化，再提交新的密码包装。任一步失败都回滚内存中的密钥，
            // 避免 UI 报错但 IsUnlocked 已经变成 true 的半状态。
            await EnsureDbInitializedAsync();

            if (!string.IsNullOrEmpty(newPassword))
            {
                _accessStore.UpdatePassword(access, newPassword, vaultKey);
                Password = newPassword;
            }
            else
            {
                Password = null;
            }

            IsSetup = true;
            _lastMasterPasswordVerifiedAt = DateTime.UtcNow;
            SaveSessionState();
        }
        catch
        {
            if (keyInstalled)
            {
                if (previousKey is null) _dbKeyService.Lock();
                else _dbKeyService.SetKey(previousKey);
            }
            throw;
        }
        finally
        {
            if (previousKey is not null)
                CryptographicOperations.ZeroMemory(previousKey);
        }
    }

    private bool TryDeriveLegacyKey(string password, out byte[] key)
    {
        key = [];
        if (!File.Exists(AppPaths.FingerprintPath)) return false;

        var fingerprintJson = File.ReadAllText(AppPaths.FingerprintPath);
        var fingerprint = JsonSerializer.Deserialize<FingerprintData>(fingerprintJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (fingerprint == null) return false;

        var salt = Convert.FromBase64String(fingerprint.Salt);
        var iterations = fingerprint.KdfIterations > 0 ? fingerprint.KdfIterations : 100_000;
        key = _crypto.DeriveKey(password, salt, iterations);
        var dbPassword = Convert.ToHexString(key);
        if (DatabaseEncryptionHelper.TryOpen(AppPaths.VaultDbPath, dbPassword)
            || DatabaseEncryptionHelper.TryOpen(AppPaths.AuthDbPath, dbPassword))
            return true;

        CryptographicOperations.ZeroMemory(key);
        key = [];
        return false;
    }

    /// <summary>
    /// 主密码成功后会静默升级设备凭据：从“缓存主密码”改成“包装随机保险库密钥”。
    /// 失败不影响本次解锁，后续可在设置页显式重试。
    /// </summary>
    private void TryAutoEnrollDeviceCredential(byte[] vaultKey)
    {
        try
        {
            if (!_bio.IsAvailable()) return;
            _bio.SetupKey();
            var encrypted = _bio.EncryptPassword("vault-key-v1:" + Convert.ToBase64String(vaultKey));
            if (string.IsNullOrEmpty(encrypted)) return;
            _bio.SaveEncryptedPassword("v2:" + encrypted);
            var id = _accessStore.GetOrCreateDeviceId();
            _accessStore.RegisterTrustedDevice(id, Environment.MachineName, "passkey");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-enroll device credential failed; password unlock remains usable");
        }
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
