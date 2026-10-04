using QRCoder;
using System.Drawing;
using System.Drawing.Imaging;

namespace BeeLogistics.Modules.Referrals.Infrastructure.Services;

public interface IQrCodeService
{
    /// <summary>
    /// Generates a QR code image as base64 string for the given URL
    /// </summary>
    string GenerateQrCodeBase64(string url, int size = 300);
    
    /// <summary>
    /// Generates a QR code image as byte array
    /// </summary>
    byte[] GenerateQrCodeBytes(string url, int size = 300);
}

public class QrCodeService : IQrCodeService
{
    public string GenerateQrCodeBase64(string url, int size = 300)
    {
        var bytes = GenerateQrCodeBytes(url, size);
        return Convert.ToBase64String(bytes);
    }

    public byte[] GenerateQrCodeBytes(string url, int size = 300)
    {
        using var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(qrCodeData);
        var qrCodeBytes = qrCode.GetGraphic(20);
        
        // If size is different, we'd need to resize, but for now return as-is
        // QRCoder's GetGraphic already handles sizing
        return qrCodeBytes;
    }
}

