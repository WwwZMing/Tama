using System.Security.Cryptography;

namespace Tama.Services.Auth;

/// <summary>
/// 最小实现的 2/3 阈值秘密共享（Shamir over GF(256)）。
/// 单份 share 不包含保险库密钥；只有达到阈值才能重建原秘密。
/// </summary>
public static class SecretSharing
{
    public sealed record Share(byte Index, byte[] Data);

    public static IReadOnlyList<Share> Split(ReadOnlySpan<byte> secret, int threshold, int shareCount)
    {
        if (secret.IsEmpty) throw new ArgumentException("Secret cannot be empty", nameof(secret));
        if (threshold < 2 || threshold > shareCount || shareCount > 255)
            throw new ArgumentOutOfRangeException(nameof(threshold));

        var shares = new Share[shareCount];
        for (var i = 0; i < shareCount; i++)
            shares[i] = new Share((byte)(i + 1), new byte[secret.Length]);

        var coefficients = new byte[threshold - 1];
        for (var offset = 0; offset < secret.Length; offset++)
        {
            RandomNumberGenerator.Fill(coefficients);
            // 最高次项不能为 0，否则这一字节的多项式实际降阶，
            // 单份 share 就可能直接等于秘密字节（概率 1/256，不能靠运气）。
            coefficients[^1] = (byte)RandomNumberGenerator.GetInt32(1, 256);
            foreach (var share in shares)
            {
                var y = secret[offset];
                var power = share.Index;
                for (var degree = 0; degree < coefficients.Length; degree++)
                {
                    y ^= Multiply(coefficients[degree], power);
                    power = Multiply(power, share.Index);
                }
                share.Data[offset] = y;
            }
        }

        return shares;
    }

    public static byte[] Combine(IEnumerable<Share> source)
    {
        var shares = source.ToArray();
        if (shares.Length < 2)
            throw new CryptographicException("至少需要两份恢复份额");
        if (shares.Select(s => s.Index).Distinct().Count() != shares.Length)
            throw new CryptographicException("恢复份额重复");
        if (shares.Any(s => s.Index == 0))
            throw new CryptographicException("恢复份额编号无效");

        var length = shares[0].Data.Length;
        if (length == 0 || shares.Any(s => s.Data.Length != length))
            throw new CryptographicException("恢复份额长度不一致");

        var secret = new byte[length];
        for (var offset = 0; offset < length; offset++)
        {
            byte value = 0;
            for (var i = 0; i < shares.Length; i++)
            {
                var numerator = (byte)1;
                var denominator = (byte)1;
                for (var j = 0; j < shares.Length; j++)
                {
                    if (i == j) continue;
                    numerator = Multiply(numerator, shares[j].Index);
                    denominator = Multiply(denominator, (byte)(shares[j].Index ^ shares[i].Index));
                }
                if (denominator == 0)
                    throw new CryptographicException("恢复份额坐标无效");
                value ^= Multiply(shares[i].Data[offset], Multiply(numerator, Inverse(denominator)));
            }
            secret[offset] = value;
        }
        return secret;
    }

    private static byte Multiply(byte a, byte b)
    {
        var result = 0;
        var x = (int)a;
        var y = (int)b;
        while (y != 0)
        {
            if ((y & 1) != 0) result ^= x;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11B;
            y >>= 1;
        }
        return (byte)result;
    }

    private static byte Inverse(byte value)
    {
        if (value == 0) throw new DivideByZeroException();
        byte result = 1;
        var factor = value;
        var exponent = 254;
        while (exponent > 0)
        {
            if ((exponent & 1) != 0) result = Multiply(result, factor);
            factor = Multiply(factor, factor);
            exponent >>= 1;
        }
        return result;
    }
}
