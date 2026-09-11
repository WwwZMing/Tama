using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tama.Core.Contracts;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Bitwarden;
using Microsoft.EntityFrameworkCore;

namespace Tama.Services.Watchtower;

/// <summary>
/// Watchtower 安全报告。HIBP 查询为 k-anonymity（只上传 SHA1 前 5 位），
/// 每个密码一次网络请求；结果按账号缓存（WatchtowerReports 表），
/// TTL 12 小时兜底，并在 AuthRefresh 同步成功后立即失效。
/// 分析范围 = 本地加密库条目 ∪ Bitwarden 同步缓存（按 Id 去重，本地优先）——
/// 无 Bitwarden 账号也能分析本地条目（缓存键 "local"）。
/// </summary>
public class WatchtowerService : IWatchtowerApi
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);
    private const string LocalCacheKey = "local";

    private readonly AuthDbContext _db;
    private readonly TamaDbContext _dbVault;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public WatchtowerService(AuthDbContext db, TamaDbContext dbVault)
    {
        _db = db;
        _dbVault = dbVault;
    }

    public async Task<WatchtowerReportResponse> Report(string? accountId = null)
    {
        var account = !string.IsNullOrEmpty(accountId)
            ? await _db.Accounts.FindAsync(accountId)
            : await _db.Accounts.FirstOrDefaultAsync(a => a.Type == "bitwarden");

        var cacheKey = account?.Id ?? LocalCacheKey;

        // 命中有效缓存直接返回（缓存存 JSON，反序列化回契约记录）
        var cached = await _db.WatchtowerReports.FindAsync(cacheKey);
        if (cached != null && DateTime.UtcNow - cached.GeneratedAt < CacheTtl)
            return JsonSerializer.Deserialize<WatchtowerReportResponse>(cached.ReportJson, JsonRpc.Options) ?? EmptyReport();

        // 分析范围：本地 Ciphers 表（明文存储）∪ Bitwarden SyncCache（按 Id 去重，本地优先）
        // 本地实体即 Tama.Core.Models.Cipher，可直接交给 AnalyzeAsync
        var localCiphers = await _dbVault.Ciphers.AsNoTracking()
            .Where(c => c.DeletedAt == null)
            .ToListAsync();
        var modelCiphers = localCiphers.ToList();

        SyncCache? cache = null;
        if (account != null)
        {
            cache = await _db.SyncCaches.AsNoTracking().FirstOrDefaultAsync(c => c.AccountId == account.Id);
            if (cache != null)
            {
                var remoteCiphers = JsonSerializer.Deserialize<List<BitwardenCipherResponse>>(cache.CiphersJson) ?? new();
                var localIds = new HashSet<Guid>(localCiphers.Select(c => c.Id));
                modelCiphers.AddRange(remoteCiphers
                    .Where(c => !Guid.TryParse(c.Id, out var gid) || !localIds.Contains(gid))
                    .Select(c => new Cipher
                    {
                        Id = Guid.TryParse(c.Id, out var gid) ? gid : Guid.NewGuid(),
                        Name = c.Name ?? "",
                        Type = (CipherType)c.Type,
                        Login = c.Login != null ? new CipherLogin
                        {
                            Username = c.Login.Username,
                            Password = c.Login.Password,
                            Totp = c.Login.Totp,
                            Uris = c.Login.Uris?.Select(u => u.Uri ?? "").ToList() ?? new(),
                        } : null,
                    }));
            }
        }

        if (modelCiphers.Count == 0)
            return EmptyReport();

        var response = await AnalyzeAsync(modelCiphers);
        var reportJson = JsonSerializer.Serialize(response, JsonRpc.Options);

        // upsert 缓存
        if (cached == null)
        {
            cached = new WatchtowerReportCache { AccountId = cacheKey, ReportJson = reportJson, GeneratedAt = DateTime.UtcNow };
            _db.WatchtowerReports.Add(cached);
        }
        else
        {
            cached.ReportJson = reportJson;
            cached.GeneratedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        return response;
    }

    /// <summary>无账号/无缓存时的空报告（满分）</summary>
    private static WatchtowerReportResponse EmptyReport() =>
        new(100, new List<SecurityIssueDto>(), new WatchtowerStatsDto(0, 0, 0, 0, 0, 0));

    /// <summary>分析条目并汇总为报告。问题清单与统计都直接用契约类型，不再有"领域模型 → DTO"的二次映射。</summary>
    public async Task<WatchtowerReportResponse> AnalyzeAsync(IEnumerable<Cipher> ciphers)
    {
        var cipherList = ciphers.ToList();
        var logins = cipherList.Where(c => c.Type == CipherType.Login && c.Login != null).ToList();
        var issues = new List<SecurityIssueDto>();

        void AddIssue(Cipher c, string type, string severity, string description) =>
            issues.Add(new SecurityIssueDto(c.Id.ToString(), c.Name, type, severity, description));

        foreach (var cipher in logins)
        {
            if (cipher.Login?.Password != null && IsWeakPassword(cipher.Login.Password))
            {
                AddIssue(cipher, "weak", "high",
                    "Password is too weak. Consider using a stronger password.");
            }
        }

        var passwordGroups = logins
            .Where(c => !string.IsNullOrEmpty(c.Login?.Password))
            .GroupBy(c => c.Login!.Password)
            .Where(g => g.Count() > 1);

        foreach (var group in passwordGroups)
        {
            foreach (var cipher in group)
            {
                AddIssue(cipher, "reused", "medium",
                    $"Password is reused across {group.Count()} items.");
            }
        }

        var breachedPasswords = new HashSet<string>();
        var passwordsToCheck = logins
            .Where(c => !string.IsNullOrEmpty(c.Login?.Password))
            .Select(c => c.Login!.Password!)
            .Distinct()
            .Take(30)
            .ToList();

        var checkTasks = passwordsToCheck.Select(async password =>
        {
            if (await IsPasswordBreachedAsync(password))
                return password;
            return null;
        });
        var results = await Task.WhenAll(checkTasks);
        foreach (var r in results.Where(r => r != null))
            breachedPasswords.Add(r!);

        foreach (var cipher in logins)
        {
            if (cipher.Login?.Password != null && breachedPasswords.Contains(cipher.Login.Password))
            {
                AddIssue(cipher, "breached", "critical",
                    "This password has appeared in a data breach. Change it immediately.");
            }
        }

        foreach (var cipher in logins)
        {
            if (string.IsNullOrEmpty(cipher.Login?.Totp))
            {
                AddIssue(cipher, "no2fa", "medium",
                    "Two-factor authentication is not configured.");
            }
        }

        foreach (var cipher in logins)
        {
            if (cipher.Login?.Uris.Any(u => u?.StartsWith("http://") == true) == true)
            {
                AddIssue(cipher, "unsecure", "low",
                    "Website uses HTTP instead of HTTPS.");
            }
        }

        var total = logins.Count;
        var criticalCount = issues.Count(i => i.Severity == "critical");
        var highCount = issues.Count(i => i.Severity == "high");
        var mediumCount = issues.Count(i => i.Severity == "medium");
        var penalty = criticalCount * 30 + highCount * 15 + mediumCount * 5;
        var score = total > 0 ? Math.Max(0, Math.Round(100.0 - (double)penalty / total * 10)) : 100;

        var stats = new WatchtowerStatsDto(
            TotalItems: cipherList.Count,
            WeakPasswords: issues.Count(i => i.Type == "weak"),
            ReusedPasswords: issues.Count(i => i.Type == "reused"),
            BreachedPasswords: issues.Count(i => i.Type == "breached"),
            No2Fa: issues.Count(i => i.Type == "no2fa"),
            Unsecure: issues.Count(i => i.Type == "unsecure"));

        return new WatchtowerReportResponse(score, issues, stats);
    }

    private async Task<bool> IsPasswordBreachedAsync(string password)
    {
        try
        {
            var sha1 = SHA1.HashData(Encoding.UTF8.GetBytes(password));
            var hash = Convert.ToHexString(sha1).ToUpper();
            var prefix = hash[..5];
            var suffix = hash[5..];

            var response = await _http.GetAsync($"https://api.pwnedpasswords.com/range/{prefix}");
            if (!response.IsSuccessStatusCode) return false;

            var body = await response.Content.ReadAsStringAsync();
            return body.Contains(suffix);
        }
        catch
        {
            return false;
        }
    }

    private bool IsWeakPassword(string password)
    {
        if (password.Length < 8) return true;
        if (password.Length < 12 && !HasComplexity(password)) return true;
        return false;
    }

    private bool HasComplexity(string password)
    {
        bool hasUpper = password.Any(char.IsUpper);
        bool hasLower = password.Any(char.IsLower);
        bool hasDigit = password.Any(char.IsDigit);
        bool hasSpecial = password.Any(c => !char.IsLetterOrDigit(c));
        int score = new[] { hasUpper, hasLower, hasDigit, hasSpecial }.Count(x => x);
        return score >= 3;
    }
}
