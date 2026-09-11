using System.Security.Cryptography;
using Tama.Core.Contracts;

namespace Tama.Services.Passwords;

/// <summary>密码强度评估、随机密码生成与 TOTP。实现 <see cref="IPasswordApi"/>。</summary>
public class PasswordService : IPasswordApi
{
    public PasswordStrengthResponse Strength(string password)
    {
        int score = 0;
        var suggestions = new List<string>();

        if (password.Length >= 20) score += 40;
        else if (password.Length >= 12) score += 25;
        else if (password.Length >= 8) score += 15;

        if (password.Length < 12) suggestions.Add("Use 12+ characters");
        if (System.Text.RegularExpressions.Regex.IsMatch(password, "[A-Z]")) score += 15;
        else suggestions.Add("Add uppercase letters");
        if (System.Text.RegularExpressions.Regex.IsMatch(password, "[a-z]")) score += 15;
        else suggestions.Add("Add lowercase letters");
        if (System.Text.RegularExpressions.Regex.IsMatch(password, "[0-9]")) score += 15;
        else suggestions.Add("Add numbers");
        if (System.Text.RegularExpressions.Regex.IsMatch(password, "[^A-Za-z0-9]")) score += 15;
        else suggestions.Add("Add special characters");
        if (new HashSet<char>(password).Count >= 10) score += 10;

        var label = score >= 80 ? "Strong" : score >= 50 ? "Fair" : "Weak";
        return new PasswordStrengthResponse(score, label, suggestions);
    }

    public PasswordGenerateResponse Generate(PasswordGenerateRequest? req)
    {
        var length = req?.Length ?? 16;
        var options = req?.Options ?? new Dictionary<string, bool>();

        var chars = "";
        if (options.GetValueOrDefault("uppercase", true)) chars += "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        if (options.GetValueOrDefault("lowercase", true)) chars += "abcdefghijklmnopqrstuvwxyz";
        if (options.GetValueOrDefault("numbers", true)) chars += "0123456789";
        if (options.GetValueOrDefault("symbols", true)) chars += "!@#$%^&*()_+-=[]{}|;:,.<>?";

        if (string.IsNullOrEmpty(chars)) chars = "abcdefghijklmnopqrstuvwxyz";

        // 拒绝采样：丢弃落在 [maxValid, 256) 的字节，消除取模偏差
        var rng = RandomNumberGenerator.Create();
        var password = new char[length];
        var buffer = new byte[1];
        var maxValid = 256 - (256 % chars.Length);

        for (int i = 0; i < length; i++)
        {
            int index;
            do
            {
                rng.GetBytes(buffer);
                index = buffer[0];
            } while (index >= maxValid);
            password[i] = chars[index % chars.Length];
        }

        return new PasswordGenerateResponse(new string(password));
    }

    /// <summary>
    /// RFC 6238 TOTP 当前验证码。secret 支持 otpauth://totp/… URI（可带 digits/period/algorithm
    /// 参数）或裸 Base32 密钥；algorithm 支持 SHA1/SHA256/SHA512（默认 SHA1，主流站点均为此）。
    /// </summary>
    public TotpNowResponse Totp(string secret, long? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(secret))
            throw new Tama.Core.Exceptions.TamaException("TOTP secret is empty");

        int digits = 6, period = 30;
        string algorithm = "SHA1";
        var raw = secret.Trim();

        if (raw.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(raw);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var s = query["secret"];
            if (string.IsNullOrEmpty(s))
                throw new Tama.Core.Exceptions.TamaException("otpauth URI missing secret");
            raw = s;
            if (int.TryParse(query["digits"], out var d) && d is >= 6 and <= 10) digits = d;
            if (int.TryParse(query["period"], out var p) && p is >= 15 and <= 120) period = p;
            if (!string.IsNullOrEmpty(query["algorithm"])) algorithm = query["algorithm"]!.ToUpperInvariant();
        }

        var key = Base32Decode(raw);
        if (key.Length == 0)
            throw new Tama.Core.Exceptions.TamaException("Invalid Base32 TOTP secret");

        var now = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long counter = now / period;
        var counterBytes = new byte[8];
        for (int i = 7; i >= 0; i--) { counterBytes[i] = (byte)(counter & 0xFF); counter >>= 8; }

        byte[] hash = algorithm switch
        {
            "SHA256" => new HMACSHA256(key).ComputeHash(counterBytes),
            "SHA512" => new HMACSHA512(key).ComputeHash(counterBytes),
            _ => new HMACSHA1(key).ComputeHash(counterBytes),
        };

        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var code = (binary % (int)Math.Pow(10, digits)).ToString().PadLeft(digits, '0');

        var remaining = period - (int)(now % period);
        return new TotpNowResponse(code, remaining, period);
    }

    /// <summary>Base32（RFC 4648）解码：容忍空格/横线/小写，忽略 padding。</summary>
    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = new List<bool>(input.Length * 5);
        foreach (var c in input.ToUpperInvariant())
        {
            if (c is ' ' or '-' or '=') continue;
            var idx = alphabet.IndexOf(c);
            if (idx < 0) return Array.Empty<byte>(); // 非法字符 → 整体无效
            for (int bit = 4; bit >= 0; bit--) bits.Add((idx & (1 << bit)) != 0);
        }
        var bytes = new byte[bits.Count / 8];
        for (int i = 0; i < bytes.Length; i++)
            for (int bit = 0; bit < 8; bit++)
                if (bits[i * 8 + bit]) bytes[i] |= (byte)(1 << (7 - bit));
        return bytes;
    }
}
