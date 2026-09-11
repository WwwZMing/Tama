using Android.App;
using Android.App.Assist;
using Android.Content;
using Android.OS;
using Android.Service.Autofill;
using Android.Views;
using Android.Views.Autofill;
using Android.Widget;

namespace Tama;

/// <summary>
/// Minimal AutofillService — 检测输入框并弹出填充建议菜单。
/// 有数据时直接填充，没数据时显示 Tama 并提供跳转 App 入口。
/// 职责拆分：字段识别/值提取 → AutofillFieldFinder；vault 数据访问 → AutofillVaultBridge。
/// </summary>
[Service(
    Name = "com.tama.app.TamaAutofillService",
    Permission = "android.permission.BIND_AUTOFILL_SERVICE",
    Exported = true)]
[IntentFilter(new[] { "android.service.autofill.AutofillService" })]
[MetaData("android.autofill", Resource = "@xml/autofill_service")]
public class TamaAutofillService : AutofillService
{
    private readonly AutofillFieldFinder _finder = new();
    private int _requestCode;

    // ---- 生命周期 ----

    public override void OnConnected()
    {
        base.OnConnected();
    }

    public override void OnDisconnected()
    {
        base.OnDisconnected();
    }

    // ---- 核心方法 ----

    public override void OnFillRequest(FillRequest request, CancellationSignal cancellationSignal, FillCallback callback)
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
            callback.OnSuccess(null!);
            return;
        }

        var fields = _finder.Find(structure);

        if (fields.Count == 0)
        {
            // 没检测到输入框 → 用 ClientState 保底（不崩溃但也不会显示服务）
            var bundle = new Bundle();
            bundle.PutString("action", "open_tama");
            callback.OnSuccess(new FillResponse.Builder().SetClientState(bundle).Build());
            return;
        }

        // 有输入框 → 构建填充响应
        var builder = new FillResponse.Builder();

        // 1. 跳转项：始终显示在最顶部，点击打开 Tama App
        var searchKey = GetSearchKey(structure);
        builder.AddDataset(MakeLaunchDataset(fields, searchKey));

        // 2. 数据项：最多 3 条凭据，显示在跳转项下方
        var credentials = GetCredentials(searchKey);
        foreach (var cred in credentials.Take(3))
        {
            builder.AddDataset(MakeCredentialDataset(fields, cred));
        }

        // SaveInfo：告诉系统 Tama 支持保存用户名+密码
        var ids = fields.Select(f => f.Id).ToArray();
        var saveInfo = new SaveInfo.Builder(
            SaveDataType.Username | SaveDataType.Password,
            ids
        ).Build();
        builder.SetSaveInfo(saveInfo!);

        callback.OnSuccess(builder.Build());
    }

    public override void OnSaveRequest(SaveRequest request, SaveCallback callback)
    {
        try
        {
            var contexts = request.FillContexts;
            if (contexts == null || contexts.Count == 0)
            {
                callback.OnSuccess();
                return;
            }

            // 从最新的 AssistStructure 提取用户实际输入的值
            var structure = contexts[^1].Structure;
            var (username, password) = _finder.ExtractFilledValues(structure);
            var searchKey = GetSearchKey(structure);

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                callback.OnSuccess();
                return;
            }

            using var bridge = AutofillVaultBridge.Create();
            bridge?.SaveOrUpdate(username, password, searchKey);
        }
        catch
        {
            // 静默失败，不阻塞用户
        }

        callback.OnSuccess();
    }

    public override void OnSavedDatasetsInfoRequest(ISavedDatasetsInfoCallback callback)
    {
        callback.OnError(0);
    }

    // ---- 构建填充数据 ----

    /// <summary>"打开 Tama"跳转项 — 始终显示在菜单顶部</summary>
    private Dataset MakeLaunchDataset(List<FoundField> fields, string searchKey)
    {
        var username = fields.FirstOrDefault(f => f.Kind == FieldKind.Username)
                    ?? fields.FirstOrDefault(f => f.Kind == FieldKind.Password);

        // 同一进程，直接写入宿主集成服务（比 Intent extras 可靠）
        if (!string.IsNullOrEmpty(searchKey))
        {
            MauiHostIntegration.Current?.SetAutofillUrl(searchKey);
        }

        var launchIntent = PackageManager!.GetLaunchIntentForPackage(PackageName!);

        var pi = PendingIntent.GetActivity(
            this,
            _requestCode++,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
        );

        var builder = new Dataset.Builder();
        if (username != null)
        {
            var views = MakeRemoteView("打开 Tama 自动填充", Resource.Mipmap.appicon);
            builder.SetValue(username.Id, AutofillValue.ForText("open_tama"), views);
        }
        if (pi != null)
            builder.SetAuthentication(pi.IntentSender!);

        return builder.Build();
    }

    /// <summary>凭据填充项 — 有点数据时显示，点击直接填</summary>
    private Dataset MakeCredentialDataset(List<FoundField> fields, Credential cred)
    {
        var username = fields.FirstOrDefault(f => f.Kind == FieldKind.Username);
        var password = fields.FirstOrDefault(f => f.Kind == FieldKind.Password);

        var builder = new Dataset.Builder();

        if (username != null)
        {
            var views = MakeRemoteView(cred.Username, null);
            builder.SetValue(username.Id, AutofillValue.ForText(cred.Username), views);
        }
        if (password != null)
        {
            builder.SetValue(password.Id, AutofillValue.ForText(cred.Password));
        }

        return builder.Build();
    }

    private RemoteViews MakeRemoteView(string text, int? iconRes)
    {
        var rv = new RemoteViews(PackageName!, Resource.Layout.autofill_dataset_item);
        rv.SetTextViewText(Resource.Id.autofill_label, text);
        if (iconRes != null)
            rv.SetImageViewResource(Resource.Id.autofill_icon, iconRes.Value);
        else
            rv.SetViewVisibility(Resource.Id.autofill_icon, ViewStates.Gone);
        return rv;
    }

    /// <summary>从 Tama 数据库查询当前域名/App 匹配的凭据</summary>
    private static List<Credential> GetCredentials(string searchKey)
    {
        if (string.IsNullOrWhiteSpace(searchKey))
            return new List<Credential>();

        using var bridge = AutofillVaultBridge.Create();
        if (bridge == null) return new List<Credential>();

        return bridge.SearchCiphers(searchKey)
            .Select(c => new Credential(c.Username, c.Password))
            .Where(c => !string.IsNullOrEmpty(c.Username))
            .Take(3)
            .ToList();
    }

    /// <summary>从 AssistStructure 提取搜索关键字（优先 WebDomain，其次包名）</summary>
    private static string GetSearchKey(AssistStructure structure)
    {
        for (int i = 0; i < structure.WindowNodeCount; i++)
        {
            var root = structure.GetWindowNodeAt(i)?.RootViewNode;
            var domain = FindWebDomain(root);
            if (!string.IsNullOrEmpty(domain)) return domain;
        }
        return structure.ActivityComponent?.PackageName ?? "";
    }

    private static string? FindWebDomain(AssistStructure.ViewNode? node)
    {
        if (node == null) return null;
        if (!string.IsNullOrEmpty(node.WebDomain)) return node.WebDomain;

        for (int i = 0; i < node.ChildCount; i++)
        {
            var domain = FindWebDomain(node.GetChildAt(i));
            if (!string.IsNullOrEmpty(domain)) return domain;
        }
        return null;
    }
}

// ---- 内部类型 ----

internal enum FieldKind { Username, Password }

internal record Credential(string Username, string Password);

internal class FoundField(AutofillId id, FieldKind kind)
{
    public AutofillId Id => id;
    public FieldKind Kind => kind;
}
