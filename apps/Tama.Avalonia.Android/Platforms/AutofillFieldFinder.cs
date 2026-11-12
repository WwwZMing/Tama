using Android.App.Assist;
using Android.Text;
using Android.Views.Autofill;

namespace Tama.Avalonia.Android.Platforms;

/// <summary>
/// 自动填充的输入框识别与值提取：autofill hints + 启发式名称 + InputType 三重匹配。
/// 同一个 kind（Username / Password）只保留第一个命中字段（去重集中在 <see cref="AddField"/>）。
/// </summary>
internal sealed class AutofillFieldFinder
{
    private const string HintUsername = "username";
    private const string HintEmailAddress = "emailAddress";
    private const string HintPassword = "password";

    /// <summary>从 AssistStructure 识别用户名/密码输入框（填充路径）。</summary>
    public List<FoundField> Find(AssistStructure structure)
    {
        var fields = new List<FoundField>();
        for (var i = 0; i < structure.WindowNodeCount; i++)
        {
            var root = structure.GetWindowNodeAt(i)?.RootViewNode;
            if (root != null) WalkNode(root, fields);
        }
        return fields;
    }

    /// <summary>从 AssistStructure 提取用户已填写的用户名/密码（保存路径）。</summary>
    public (string? Username, string? Password) ExtractFilledValues(AssistStructure structure)
    {
        string? username = null, password = null;
        for (var i = 0; i < structure.WindowNodeCount; i++)
        {
            var root = structure.GetWindowNodeAt(i)?.RootViewNode;
            ExtractValuesFromNode(root, ref username, ref password);
        }
        return (username, password);
    }

    // ---- 填充路径：字段识别 ----

    private void WalkNode(AssistStructure.ViewNode node, List<FoundField> fields)
    {
        // 1) 标准 autofill hints（浏览器的 <input autocomplete> 走这条）
        var hints = node.GetAutofillHints();
        if (hints != null)
        {
            var hintSet = new HashSet<string>(hints);
            if (hintSet.Contains(HintUsername) || hintSet.Contains(HintEmailAddress))
                AddField(fields, FieldKind.Username, node.AutofillId!);
            else if (hintSet.Contains(HintPassword))
                AddField(fields, FieldKind.Password, node.AutofillId!);
        }

        // 2) 启发式：hint / resource-id 名称
        MatchByName(node, fields);

        // 3) 类型匹配：EditText + InputType
        MatchByInputType(node, fields);

        for (var i = 0; i < node.ChildCount; i++)
        {
            var child = node.GetChildAt(i);
            if (child != null) WalkNode(child, fields);
        }
    }

    private static void MatchByName(AssistStructure.ViewNode node, List<FoundField> fields)
    {
        var text = ((node.Hint ?? "") + (node.IdEntry ?? "")).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text)) return;

        if (HasKeyword(text, "username", "email", "login", "account", "phone", "mobile"))
            AddField(fields, FieldKind.Username, node.AutofillId!);
        if (HasKeyword(text, "password", "passwd", "pwd"))
            AddField(fields, FieldKind.Password, node.AutofillId!);
    }

    private static void MatchByInputType(AssistStructure.ViewNode node, List<FoundField> fields)
    {
        var className = (node.ClassName ?? "").ToLowerInvariant();
        if (!className.Contains("edittext") && !className.Contains("textinput")) return;

        var inputType = (int)node.InputType;
        // ⚠ 必须写 InputTypes（已 using Android.Text）而不是 Android.Text.InputTypes：
        //   根命名空间被我们自己的 Tama.Avalonia.Android 占了，限定写会解析成 Tama.Avalonia.Android.Text。
        var isPassword = (inputType & (int)InputTypes.TextVariationPassword) != 0
                      || (inputType & (int)InputTypes.TextVariationWebPassword) != 0
                      || (inputType & (int)InputTypes.NumberVariationPassword) != 0;
        if (isPassword)
        {
            AddField(fields, FieldKind.Password, node.AutofillId!);
            return;
        }

        // 所有普通输入框都可能是用户名（兜底，保证总有机会匹配）
        AddField(fields, FieldKind.Username, node.AutofillId!);
    }

    private static void AddField(List<FoundField> fields, FieldKind kind, AutofillId id)
    {
        if (!fields.Any(f => f.Kind == kind))
            fields.Add(new FoundField(id, kind));
    }

    // ---- 保存路径：值提取 ----

    private static void ExtractValuesFromNode(AssistStructure.ViewNode? node, ref string? username, ref string? password)
    {
        if (node == null) return;

        var text = node.Text?.ToString();
        if (string.IsNullOrEmpty(text)) text = node.AutofillValue?.TextValue;

        if (!string.IsNullOrEmpty(text))
        {
            var hints = node.GetAutofillHints();
            if (hints != null)
            {
                var set = new HashSet<string>(hints);
                if (username == null && (set.Contains(HintUsername) || set.Contains(HintEmailAddress)))
                    username = text;
                else if (password == null && set.Contains(HintPassword))
                    password = text;
            }

            // 与填充路径同一套关键词，免得"能填进去但存不下来"
            var idText = ((node.Hint ?? "") + (node.IdEntry ?? "")).ToLowerInvariant();
            if (username == null && HasKeyword(idText, "username", "email", "login", "account"))
                username = text;
            if (password == null && HasKeyword(idText, "password", "passwd", "pwd"))
                password = text;
        }

        for (var i = 0; i < node.ChildCount; i++)
            ExtractValuesFromNode(node.GetChildAt(i), ref username, ref password);
    }

    private static bool HasKeyword(string text, params string[] keywords)
        => keywords.Any(k => text.Contains(k));
}

// ---- 内部类型 ----

internal enum FieldKind { Username, Password }

internal sealed record FoundField(AutofillId Id, FieldKind Kind);
