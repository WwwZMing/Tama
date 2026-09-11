using Tama.Core.Contracts;
using Tama.Services.Passwords;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// RFC 6238 官方测试向量（secret = ASCII "12345678901234567890"，Base32:
/// GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ；SHA1/8 位）+ otpauth:// 解析 + Base32 容错。
/// </summary>
public class TotpTests
{
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private readonly PasswordService _svc = new();

    private TotpNowResponse At(long unixSeconds, string? secret = null, int digits = 8) =>
        _svc.Totp(
            secret ?? $"otpauth://totp/test?secret={RfcSecret}&digits={digits}&period=30",
            unixSeconds);

    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1111111111, "14050471")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    public void Rfc6238_Vectors_Pass(long time, string expected)
    {
        var r = At(time);
        Assert.Equal(expected, r.Code);
    }

    [Fact]
    public void Bare_Base32_Secret_Defaults_6Digits_30s()
    {
        // 与 RFC 向量同源：time=59, counter=1 → 8 位为 94287082 → 6 位取后 6 位 287082
        var r = _svc.Totp(RfcSecret, 59);
        Assert.Equal("287082", r.Code);
        Assert.Equal(6, r.Code.Length);
        Assert.Equal(30, r.Period);
        Assert.Equal(1, r.SecondsRemaining); // 59 % 30 = 29 → 距下次刷新剩 1 秒
    }

    [Theory]
    [InlineData("gezdgnbvgy3tqojqgezdgnbvgy3tqojq")] // 小写
    [InlineData("GEZD GNBV GY3T QOJQ GEZD GNBV GY3T QOJQ")] // 空格
    [InlineData("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ====")] // padding
    public void Base32_Tolerates_Formatting(string secret)
    {
        var r = _svc.Totp(secret, 59);
        Assert.Equal("287082", r.Code);
    }

    [Fact]
    public void Invalid_Secret_Throws()
    {
        Assert.ThrowsAny<Exception>(() => _svc.Totp("not!!base32!!", 59));
        Assert.ThrowsAny<Exception>(() => _svc.Totp(" ", 59));
    }

    [Fact]
    public void Otpauth_Period_And_Digits_Are_Honored()
    {
        // period=60 → counter=59/60=0；digits 从 URI 读取
        var r = _svc.Totp($"otpauth://totp/x?secret={RfcSecret}&digits=7&period=60", 59);
        Assert.Equal(60, r.Period);
        Assert.Equal(7, r.Code.Length);
    }
}
