using Tama.Core;

namespace Tama.Avalonia.Services;

/// <summary>
/// 界面偏好的落盘读写（一个偏好一个文件，和 <see cref="ThemeService"/> 的做法一致）。
///
/// 存在的理由：Blazor 版把这类开关放 localStorage，Avalonia 没有 JS 层；
/// 塞进数据库又要求"解锁后才读得到"，而偏好恰恰要在解锁前生效（比如要不要自动弹验证）。
/// </summary>
public static class UiPrefs
{
    /// <summary>
    /// 进解锁页时自动弹一次系统验证（Windows Hello / 指纹）。
    /// 默认**开启**——这是旧版的行为；关掉的人多半要反复调试，或被系统策略限制。
    /// </summary>
    public static bool AutoBioPrompt
    {
        get
        {
            try
            {
                return !(File.Exists(AppPaths.AutoBioPath)
                         && File.ReadAllText(AppPaths.AutoBioPath).Trim().Equals("off", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                // 读不出来按开启处理：这只是个便利开关，不该因文件损坏而改变行为
                return true;
            }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(AppPaths.AutoBioPath, value ? "on" : "off");
            }
            catch (Exception ex)
            {
                // 写不进去只意味着"下次启动恢复默认"，不值得打断用户
                Serilog.Log.Debug(ex, "Failed to persist auto-bio preference");
            }
        }
    }
}
