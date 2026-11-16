using Android.App;
using Android.App.Assist;
using Android.Content;
using Android.OS;
using Android.Service.Autofill;
using Android.Views;
using Android.Views.Autofill;
using Android.Widget;
using Tama.Avalonia.Services;

using AppResource = Tama.Avalonia.Android.Resource;
using TamaApp = Tama.Avalonia.App;

// Dataset.Builder.SetValue 带 RemoteViews 的重载在 API 33+ 标记为"过时"（deprecated，不是移除）。
// 我们就是要它：只有 RemoteViews 能在系统自动填充下拉里显示自定义文字（域名/用户名）。
// 不加这条抑制的话构建会出现 3 条 CA1422，而它们对应的行为与旧 MAUI 实现完全一致。
#pragma warning disable CA1422

namespace Tama.Avalonia.Android.Platforms;

/// <summary>
/// 系统级自动填充服务：在别的应用/浏览器的输入框上弹出"填充 / 打开 Tama"菜单。
///
/// 与 MAUI 版的三处差异（都是 Avalonia 迁移带来的，不是行为变化）：
///   1. 依赖注入来源从 <c>IPlatformApplication.Current.Services</c> 换成
///      <see cref="Tama.Avalonia.App.HostEnvironment"/>（同一个进程，同样的作用域语义）；
///   2. 跳转目标从 <c>MauiHostIntegration.Current.SetAutofillUrl</c> 换成
///      <see cref="AutofillRequest"/>（壳里的 <c>VaultViewModel</c> 解锁后一次性消费，
///      把搜索框预填成当前域名）；
///   3. 数据项图标换成这个工程的 drawable（MAUI 的 mipmap appicon 不存在了）。
///
/// 安全边界：**锁着的时候什么也不填**。FillResponse 的数据项来自 <see cref="AutofillVaultBridge"/>，
/// 拿不到已解锁的库就只返回"打开 Tama"这一项——绝不在这里想办法绕过主密码。
/// </summary>
[Service(
    Name = "com.tama.app.TamaAutofillService",
    Label = "Tama 自动填充",
    Permission = "android.permission.BIND_AUTOFILL_SERVICE",
    Exported = true)]
[IntentFilter(new[] { "android.service.autofill.AutofillService" })]
[MetaData("android.autofill", Resource = "@xml/autofill_service")]
public sealed class TamaAutofillService : AutofillService
{
    private readonly AutofillFieldFinder _finder = new();
    private int _requestCode;

    public override void OnFillRequest(FillRequest request, CancellationSignal cancellationSignal, FillCallback callback)
    {
        try
        {
            var contexts = request.FillContexts;
            if (contexts == null || contexts.Count == 0)
            {
                callback.OnFailure("No fill contexts");
                return;
            }

            var structure = contexts[^1].Structure;
            if (structure == null)
            {
                callback.OnSuccess(null!); // null = 不显示系统自动填充菜单
                return;
            }

            // Tama 自己的密码框、搜索框不应再被系统自动填充菜单盖住。
            // 自服务留在候选列表里会导致"在密码管理器里输入主密码时又弹密码管理器"。
            if (string.Equals(structure.ActivityComponent?.PackageName, PackageName, StringComparison.Ordinal))
            {
                callback.OnSuccess(null!);
                return;
            }

            var fields = _finder.Find(structure);
            var searchKey = GetSearchKey(structure);

            if (fields.Count == 0)
            {
                // 普通文本框、搜索框和 Tama 自己都不响应。返回 null 才不会在字段旁弹空菜单。
                callback.OnSuccess(null!);
                return;
            }

            var builder = new FillResponse.Builder();

            // 1) 跳转项：永远排在最前，点了打开 Tama（并把域名带过去做预填）
            builder.AddDataset(MakeLaunchDataset(fields, searchKey));

            // 2) 数据项：最多 3 条凭据，直接点就填
            var credentials = GetCredentials(searchKey);
            foreach (var cred in credentials.Take(3))
                builder.AddDataset(MakeCredentialDataset(fields, cred));

            // 告诉系统"我们能接住用户名+密码的保存请求"（保存回调见 OnSaveRequest）
            var ids = fields.Select(f => f.Id).ToArray();
            var saveInfo = new SaveInfo.Builder(SaveDataType.Username | SaveDataType.Password, ids).Build();
            builder.SetSaveInfo(saveInfo!);

            callback.OnSuccess(builder.Build());
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Autofill fill request failed");
            callback.OnFailure("Tama autofill failed");
        }
    }

    public override void OnSaveRequest(SaveRequest request, SaveCallback callback)
    {
        try
        {
            var contexts = request.FillContexts;
            if (contexts != null && contexts.Count > 0)
            {
                var structure = contexts[^1].Structure;
                var (username, password) = _finder.ExtractFilledValues(structure!);
                var searchKey = GetSearchKey(structure!);

                if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                {
                    using var bridge = AutofillVaultBridge.Create();
                    bridge?.SaveOrUpdate(username!, password!, searchKey);
                }
            }
        }
        catch (Exception ex)
        {
            // 保存失败绝不能让用户被一个弹窗打断
            Serilog.Log.Warning(ex, "Autofill save request failed");
        }

        callback.OnSuccess();
    }

    // ---- 数据集构造 ----

    /// <summary>"打开 Tama" 跳转项（排在菜单最上面）。</summary>
    private Dataset MakeLaunchDataset(List<FoundField> fields, string searchKey)
    {
        var anchor = fields.FirstOrDefault(f => f.Kind == FieldKind.Username)
                     ?? fields.FirstOrDefault(f => f.Kind == FieldKind.Password);

        // 同一个进程：把目标塞给 UI 层，解锁后保险库页会拿它预填搜索框。
        // 拿不到宿主（极早期）就静默丢掉——比崩溃好。
        if (!string.IsNullOrEmpty(searchKey))
        {
            var pending = TamaApp.HostEnvironment?.Host.Services.GetService(typeof(AutofillRequest))
                as AutofillRequest;
            pending?.Push(searchKey);
        }

        var launchIntent = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        var builder = new Dataset.Builder();

        if (anchor != null)
        {
            var views = new RemoteViews(PackageName!, AppResource.Layout.autofill_dataset_item);
            views.SetTextViewText(AppResource.Id.autofill_label, "打开 Tama 自动填充");
            views.SetImageViewResource(AppResource.Id.autofill_icon, AppResource.Drawable.Icon);
            builder.SetValue(anchor.Id, AutofillValue.ForText("open_tama")!, views);
        }

        if (launchIntent != null)
        {
            var pendingIntent = PendingIntent.GetActivity(
                this,
                _requestCode++,
                launchIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            if (pendingIntent != null)
                builder.SetAuthentication(pendingIntent.IntentSender!);
        }

        return builder.Build();
    }

    /// <summary>一条凭据 = 一个数据项，点了直接填进用户名/密码框。</summary>
    private Dataset MakeCredentialDataset(List<FoundField> fields, VaultCredential cred)
    {
        var username = fields.FirstOrDefault(f => f.Kind == FieldKind.Username);
        var password = fields.FirstOrDefault(f => f.Kind == FieldKind.Password);

        var builder = new Dataset.Builder();

        if (username != null)
        {
            var views = new RemoteViews(PackageName!, AppResource.Layout.autofill_dataset_item);
            views.SetTextViewText(AppResource.Id.autofill_label, cred.Username);
            views.SetViewVisibility(AppResource.Id.autofill_icon, ViewStates.Gone);
            builder.SetValue(username.Id, AutofillValue.ForText(cred.Username)!, views);
        }

        if (password != null)
            builder.SetValue(password.Id, AutofillValue.ForText(cred.Password));

        return builder.Build();
    }

    private static List<VaultCredential> GetCredentials(string searchKey)
    {
        if (string.IsNullOrWhiteSpace(searchKey)) return new List<VaultCredential>();

        using var bridge = AutofillVaultBridge.Create();
        if (bridge == null) return new List<VaultCredential>();

        return bridge.SearchCiphers(searchKey)
            .Where(c => !string.IsNullOrEmpty(c.Username) && !string.IsNullOrEmpty(c.Password))
            .Select(c => new VaultCredential(c.Username, c.Password))
            .Take(3)
            .ToList();
    }

    // ---- 目标识别 ----

    /// <summary>搜索关键字：优先网页域名，其次 Activity 的包名。</summary>
    private static string GetSearchKey(AssistStructure structure)
    {
        for (var i = 0; i < structure.WindowNodeCount; i++)
        {
            var root = structure.GetWindowNodeAt(i)?.RootViewNode;
            var domain = FindWebDomain(root);
            if (!string.IsNullOrEmpty(domain)) return domain!;
        }
        return structure.ActivityComponent?.PackageName ?? "";
    }

    private static string? FindWebDomain(AssistStructure.ViewNode? node)
    {
        if (node == null) return null;
        if (!string.IsNullOrEmpty(node.WebDomain)) return node.WebDomain;

        for (var i = 0; i < node.ChildCount; i++)
        {
            var domain = FindWebDomain(node.GetChildAt(i));
            if (!string.IsNullOrEmpty(domain)) return domain;
        }
        return null;
    }
}

/// <summary>填充用的一条凭据。</summary>
internal sealed record VaultCredential(string Username, string Password);

#pragma warning restore CA1422
