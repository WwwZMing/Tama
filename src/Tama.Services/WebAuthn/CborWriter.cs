using System.Text;

namespace Tama.Services.WebAuthn;

/// <summary>
/// 最小 CBOR 编码器（RFC 8949 子集）。
/// 只为 WebAuthn 需要的结构服务：map / byte string / text string / int / array / bool。
/// 手写而非引库：零依赖、可控，且项目目标结构固定。
/// </summary>
public sealed class CborWriter
{
    private readonly MemoryStream _ms = new();

    public byte[] ToArray() => _ms.ToArray();

    public void WriteInt(long value)
    {
        if (value >= 0)
        {
            if (value < 24) _ms.WriteByte((byte)value);
            else if (value <= byte.MaxValue) { _ms.WriteByte(0x18); _ms.WriteByte((byte)value); }
            else if (value <= ushort.MaxValue) { _ms.WriteByte(0x19); WriteUInt16BE((ushort)value); }
            else { _ms.WriteByte(0x1a); WriteUInt32BE((uint)value); }
        }
        else
        {
            // major type 1：编码 n = -1 - value（无符号）
            var n = -1L - value;
            if (n < 24) _ms.WriteByte((byte)(0x20 + n));
            else if (n <= byte.MaxValue) { _ms.WriteByte(0x38); _ms.WriteByte((byte)n); }
            else if (n <= ushort.MaxValue) { _ms.WriteByte(0x39); WriteUInt16BE((ushort)n); }
            else { _ms.WriteByte(0x3a); WriteUInt32BE((uint)n); }
        }
    }

    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        WriteLength(0x40, data.Length);
        _ms.Write(data);
    }

    public void WriteText(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteLength(0x60, bytes.Length);
        _ms.Write(bytes);
    }

    public void WriteArrayStart(int count) => WriteLength(0x80, count);

    public void WriteMapStart(int count) => WriteLength(0xa0, count);

    public void WriteBool(bool b) => _ms.WriteByte(b ? (byte)0xf5 : (byte)0xf4);

    public void WriteNull() => _ms.WriteByte(0xf6);

    /// <summary>major type 前缀 + 长度（覆盖 0..65535，WebAuthn 结构足够）</summary>
    private void WriteLength(byte major, int length)
    {
        if (length < 24) _ms.WriteByte((byte)(major + length));
        else if (length <= byte.MaxValue) { _ms.WriteByte((byte)(major + 0x18)); _ms.WriteByte((byte)length); }
        else { _ms.WriteByte((byte)(major + 0x19)); WriteUInt16BE((ushort)length); }
    }

    private void WriteUInt16BE(ushort v)
    {
        _ms.WriteByte((byte)(v >> 8));
        _ms.WriteByte((byte)v);
    }

    private void WriteUInt32BE(uint v)
    {
        _ms.WriteByte((byte)(v >> 24));
        _ms.WriteByte((byte)(v >> 16));
        _ms.WriteByte((byte)(v >> 8));
        _ms.WriteByte((byte)v);
    }
}
