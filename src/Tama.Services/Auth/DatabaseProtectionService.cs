using System.Security.Cryptography;
using Serilog;

namespace Tama.Services.Auth;

public class DatabaseProtectionService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DatabaseProtectionService>();

    /// <summary>
    /// 设置数据库文件权限，只允许当前用户访问（仅 Windows）。
    /// 非 Windows 平台依赖文件系统自身的权限模型（如 Unix umask / macOS sandbox）。
    /// </summary>
    public void SetFilePermissions(string dbPath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(dbPath))
            return;

        try
        {
            var fileInfo = new FileInfo(dbPath);
            var fileSecurity = fileInfo.GetAccessControl();

            var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;

            fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var rule = new System.Security.AccessControl.FileSystemAccessRule(
                currentUser,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow);
            fileSecurity.AddAccessRule(rule);

            fileInfo.SetAccessControl(fileSecurity);
            Log.Information("Set file permissions for {Path} to {User}", dbPath, currentUser);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set file permissions for {Path}", dbPath);
        }
    }

    /// <summary>
    /// 计算文件的SHA256哈希并保存
    /// </summary>
    public void SaveIntegrityHash(string dbPath)
    {
        if (!File.Exists(dbPath))
            return;

        try
        {
            var hash = ComputeHash(dbPath);
            var hashPath = dbPath + ".sha256";
            File.WriteAllBytes(hashPath, hash);
            Log.Information("Saved integrity hash for {Path}", dbPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save integrity hash for {Path}", dbPath);
        }
    }

    /// <summary>
    /// 验证数据库文件完整性
    /// </summary>
    public bool VerifyIntegrity(string dbPath)
    {
        var hashPath = dbPath + ".sha256";

        if (!File.Exists(dbPath) || !File.Exists(hashPath))
            return true; // 没有哈希文件，跳过验证

        try
        {
            var savedHash = File.ReadAllBytes(hashPath);
            var currentHash = ComputeHash(dbPath);

            if (!savedHash.SequenceEqual(currentHash))
            {
                Log.Error("Database integrity check failed for {Path}", dbPath);
                return false;
            }

            Log.Information("Integrity verified for {Path}", dbPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to verify integrity for {Path}", dbPath);
            return true; // 验证失败不阻止启动
        }
    }

    /// <summary>
    /// 保护数据库文件（权限 + 完整性校验）
    /// </summary>
    public void ProtectDatabase(string dbPath)
    {
        SetFilePermissions(dbPath);
        SaveIntegrityHash(dbPath);
    }

    /// <summary>
    /// 验证数据库完整性，如果失败返回false
    /// </summary>
    public bool ValidateDatabase(string dbPath)
    {
        return VerifyIntegrity(dbPath);
    }

    private static byte[] ComputeHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return sha256.ComputeHash(stream);
    }
}
