using QRCoder;

namespace ClassIsland.Automation.Core.Services;

/// <summary>
/// 二维码生成服务，使用轻量无依赖的 QRCoder 生成纯 PNG 字节流，支持 Avalonia 和 WPF 跨框架渲染。
/// </summary>
public static class QrCodeService
{
    /// <summary>
    /// 将文本内容生成为 PNG 格式图片的字节数组
    /// </summary>
    /// <param name="content">待编码的内容（如 otpauth:// 协议 URI）</param>
    /// <param name="pixelsPerModule">每个二维码模块像素大小（默认 8 像素）</param>
    /// <returns>PNG 图像原始二进制数据</returns>
    public static byte[] GeneratePngBytes(string content, int pixelsPerModule = 8)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<byte>();
        }

        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var qrCode = new PngByteQRCode(qrCodeData);
        return qrCode.GetGraphic(pixelsPerModule);
    }
}
