using System.Security.Cryptography;
using System.Text;

namespace Tama.Services.Auth;

/// <summary>
/// 人可保存、程序可校验的文本份额格式。故意不含未加密的保险库密钥。
/// </summary>
public static class RecoveryShareCodec
{
    public const string Format = "TAMA-RECOVERY-SHARE/1";

    public sealed record ParsedShare(string SetId, byte Index, byte Threshold, byte Total, byte[] Data);

    public static string Encode(string setId, byte index, byte threshold, byte total, byte[] data)
    {
        Validate(setId, index, threshold, total, data);
        var body = $"{setId}|{index}|{threshold}|{total}|{Base64Url(data)}";
        var check = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body))[..16])
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return string.Join('\n',
            Format,
            "这是一份 Tama 受信恢复份额。单独一份不能打开保险库。",
            "请与其它份额分开保存，不要拍照或和电脑放在同一个地方。",
            $"SET:{setId}",
            $"INDEX:{index}",
            $"THRESHOLD:{threshold}",
            $"TOTAL:{total}",
            $"DATA:{Base64Url(data)}",
            $"CHECK:{check}");
    }

    public static ParsedShare Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16_384)
            throw new CryptographicException("恢复份额为空或过大");

        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries);
        if (lines.Length < 9 || lines[0] != Format)
            throw new CryptographicException("不是 Tama 恢复份额");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(3))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            fields[line[..colon]] = line[(colon + 1)..];
        }

        var setId = Required(fields, "SET");
        var index = Byte(fields, "INDEX");
        var threshold = Byte(fields, "THRESHOLD");
        var total = Byte(fields, "TOTAL");
        var data = FromBase64Url(Required(fields, "DATA"));
        var check = Required(fields, "CHECK");

        Validate(setId, index, threshold, total, data);
        var body = $"{setId}|{index}|{threshold}|{total}|{Base64Url(data)}";
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body))[..16])
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(check), Encoding.ASCII.GetBytes(expected)))
            throw new CryptographicException("恢复份额校验失败，文件可能损坏或被修改");

        return new ParsedShare(setId, index, threshold, total, data);
    }

    private static void Validate(string setId, byte index, byte threshold, byte total, byte[] data)
    {
        if (string.IsNullOrWhiteSpace(setId) || setId.Length > 64)
            throw new CryptographicException("恢复份额集合编号无效");
        if (index == 0 || total < 2 || threshold < 2 || threshold > total || index > total)
            throw new CryptographicException("恢复份额参数无效");
        if (data.Length is < 16 or > 128)
            throw new CryptographicException("恢复份额数据长度无效");
    }

    private static string Required(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new CryptographicException($"恢复份额缺少 {key}");

    private static byte Byte(Dictionary<string, string> fields, string key) =>
        byte.TryParse(Required(fields, key), out var value)
            ? value
            : throw new CryptographicException($"恢复份额 {key} 无效");

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }
}
