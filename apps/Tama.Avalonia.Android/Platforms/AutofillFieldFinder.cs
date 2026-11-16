using Android.App.Assist;
using Android.Text;
using Android.Views.Autofill;

namespace Tama.Avalonia.Android.Platforms;

/// <summary>
/// 自动填充的输入框识别与值提取：autofill hints + 启发式名称 + InputType 三重匹配。
/// 同一个 kind（Username / Password）只保留第一个命中字段（去重集中在 <see cref="AddField"/>）。
///
/// 关键边界：普通 EditText **不再默认当用户名框**。以前任何输入框（搜索、昵称、备注）
/// 都会触发展开自动填充，用户看到的就是"走到哪弹到哪"。现在只有标准提示、
/// 明确的用户名/密码命名或密码输入类型才算；仅靠名称猜出的用户名框还必须与密码框同屏。
/// </summary>
internal sealed class AutofillFieldFinder
{
    private const string HintUsername = "username";
    private const string HintEmailAddress = "emailAddress";
    private const string HintPassword = "password";

    /// <summary>从 AssistStructure 识别用户名/密码输入框（填充路径）。</summary>
    public List<FoundField> Find(AssistStructure structure)
    {
        var candidates = new List<FoundField>();
        for (var i = 0; i < structure.WindowNodeCount; i++)
        {
            var root = structure.GetWindowNodeAt(i)?.RootViewNode;
            if (root != null) WalkNode(root, candidates);
        }

        // 密码输入类型/标准 password hint 是硬信号。只有这种真正的登录表单才接受
        // "名字像用户名"的弱信号；单字段页只有显式 username hint 才算登录，
        // 单纯 email 字段可能是订阅框/联系人页，不能单独触弹窗。
        var password = candidates.FirstOrDefault(f => f.Kind == FieldKind.Password);
        var username = candidates.FirstOrDefault(f => f.Kind == FieldKind.Username &&
            (f.Source == FieldSource.UsernameHint || password != null));

        var fields = new List<FoundField>(2);
        if (username != null) fields.Add(username);
        if (password != null) fields.Add(password);
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
            if (hintSet.Contains(HintUsername))
                AddField(fields, FieldKind.Username, node.AutofillId!, FieldSource.UsernameHint);
            else if (hintSet.Contains(HintEmailAddress))
                AddField(fields, FieldKind.Username, node.AutofillId!, FieldSource.EmailHint);
            else if (hintSet.Contains(HintPassword))
                AddField(fields, FieldKind.Password, node.AutofillId!, FieldSource.PasswordHint);
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

        if (HasKeyword(text, "username", "email", "login", "account"))
            AddField(fields, FieldKind.Username, node.AutofillId!, FieldSource.HeuristicName);
        if (HasKeyword(text, "password", "passwd", "pwd"))
            AddField(fields, FieldKind.Password, node.AutofillId!, FieldSource.HeuristicName);
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
            AddField(fields, FieldKind.Password, node.AutofillId!, FieldSource.InputType);
        }
    }

    private static void AddField(List<FoundField> fields, FieldKind kind, AutofillId id, FieldSource source)
    {
        var existingIndex = fields.FindIndex(f => f.Kind == kind);
        if (existingIndex < 0)
            fields.Add(new FoundField(id, kind, source));
        else if (SourcePriority(source) > SourcePriority(fields[existingIndex].Source))
            fields[existingIndex] = new FoundField(id, kind, source);
    }

    /// <summary>同一类字段命中多次时，明确提示优先于命名猜测，避免早出现一个模糊输入框就占坑。</summary>
    private static int SourcePriority(FieldSource source) => source switch
    {
        FieldSource.UsernameHint or FieldSource.PasswordHint => 3,
        FieldSource.EmailHint or FieldSource.InputType => 2,
        FieldSource.HeuristicName => 1,
        _ => 0,
    };

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

internal enum FieldSource { UsernameHint, EmailHint, PasswordHint, HeuristicName, InputType }

internal sealed record FoundField(AutofillId Id, FieldKind Kind, FieldSource Source);
