using Avalonia.Media.Imaging;
using QRCoder;

namespace Tama.Avalonia.Services;

/// <summary>完全在本地把配对 URI 渲染为二维码，不调用在线图片服务。</summary>
public static class QrCodeImageService
{
    public static Bitmap Create(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("QR payload required", nameof(payload));

        var png = PngByteQRCodeHelper.GetQRCode(
            payload,
            QRCodeGenerator.ECCLevel.M,
            8,
            true);
        using var stream = new MemoryStream(png, writable: false);
        return new Bitmap(stream);
    }
}
