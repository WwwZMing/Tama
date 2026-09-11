namespace Tama.Core.Contracts;

/// <summary>
/// 密码工具：强度评估、生成、TOTP 验证码。
/// 实现：<c>Tama.Services.Passwords.PasswordService</c>。
/// </summary>
public interface IPasswordApi
{
    /// <summary>评估密码强度（本地启发式，不出网）。</summary>
    PasswordStrengthResponse Strength(string password);

    /// <summary>生成密码或密码短语。</summary>
    PasswordGenerateResponse Generate(PasswordGenerateRequest req);

    /// <summary>
    /// 计算 TOTP 验证码。<paramref name="secret"/> 支持 otpauth:// URI 或裸 Base32。
    /// <paramref name="timestamp"/> 仅供测试注入向量，null 表示当前时间。
    /// </summary>
    TotpNowResponse Totp(string secret, long? timestamp = null);
}

/// <summary>生成参数。<c>Options</c> 为字符集开关（如 uppercase/lowercase/digits/symbols）。</summary>
public sealed record PasswordGenerateRequest
{
    public int Length { get; init; } = 16;
    public Dictionary<string, bool>? Options { get; init; }
}

/// <summary>强度评估结果。Score 为 0-100。</summary>
public sealed record PasswordStrengthResponse(int Score, string Label, List<string> Suggestions);

/// <summary>生成结果。</summary>
public sealed record PasswordGenerateResponse(string Password);

/// <summary>TOTP 当前验证码与剩余有效秒数。</summary>
public sealed record TotpNowResponse(string Code, int SecondsRemaining, int Period);
