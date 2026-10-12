using System.Security.Cryptography;
using System.Text;

namespace ClassIsland.Automation.Core.Services;

/// <summary>
/// 标准 TOTP (RFC 6238) 与 Base32 编码实现，支持与各类 Authenticator App 及智能手表/手环（如小米手环、华为手环）离线验证码完美兼容。
/// </summary>
public static class TotpService
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// 生成符合工业规范的 20 字节（160 位）随机 Base32 TOTP 密钥
    /// </summary>
    public static string GenerateSecret(int byteLength = 20)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(byteLength);
        return ToBase32(bytes);
    }

    /// <summary>
    /// 生成标准的 otpauth:// 协议 URI，用于生成二维码供验证器扫码绑定
    /// </summary>
    public static string GenerateOtpAuthUri(string secret, string account = "ClassIsland", string issuer = "CIAuto")
    {
        string cleanSecret = CleanSecret(secret);
        return $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={cleanSecret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";
    }

    /// <summary>
    /// 校验用户输入的 6 位动态验证码。默认容忍 ±1 个时间窗口（±30 秒），防止手环或班级电脑存在轻微时钟漂移。
    /// </summary>
    public static bool VerifyTotp(string secret, string inputCode, int allowedDriftSteps = 1)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(inputCode))
        {
            return false;
        }

        string cleanCode = inputCode.Trim().Replace(" ", "");
        if (cleanCode.Length != 6 || !cleanCode.All(char.IsDigit))
        {
            return false;
        }

        string cleanSecret = CleanSecret(secret);
        long currentCounter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

        for (int i = -allowedDriftSteps; i <= allowedDriftSteps; i++)
        {
            string expected = ComputeHotp(cleanSecret, currentCounter + i);
            if (string.Equals(expected, cleanCode, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 根据当前时间计算 6 位动态验证码
    /// </summary>
    public static string ComputeCurrentTotp(string secret)
    {
        long currentCounter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        return ComputeHotp(CleanSecret(secret), currentCounter);
    }

    private static string ComputeHotp(string base32Secret, long counter)
    {
        byte[] key = FromBase32(base32Secret);
        if (key.Length == 0) return "000000";

        byte[] counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        using var hmac = new HMACSHA1(key);
        byte[] hash = hmac.ComputeHash(counterBytes);

        int offset = hash[hash.Length - 1] & 0x0F;
        int binaryCode = ((hash[offset] & 0x7F) << 24)
                       | ((hash[offset + 1] & 0xFF) << 16)
                       | ((hash[offset + 2] & 0xFF) << 8)
                       | (hash[offset + 3] & 0xFF);

        int code = binaryCode % 1000000;
        return code.ToString("D6");
    }

    /// <summary>
    /// 将密钥格式化为以 4 字符一组呈现的字符串（如 JBSW Y3DP EHPK...），极大方便在手环或手机上肉眼校对与手动输入
    /// </summary>
    public static string FormatSecretForDisplay(string secret)
    {
        string clean = CleanSecret(secret);
        if (string.IsNullOrEmpty(clean)) return "";

        var sb = new StringBuilder();
        for (int i = 0; i < clean.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                sb.Append(' ');
            }
            sb.Append(clean[i]);
        }
        return sb.ToString();
    }

    public static string CleanSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return "";
        return secret.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "");
    }

    public static string ToBase32(byte[] data)
    {
        if (data == null || data.Length == 0) return "";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bitsLeft = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }
        if (bitsLeft > 0)
        {
            sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }
        return sb.ToString();
    }

    public static byte[] FromBase32(string base32)
    {
        if (string.IsNullOrWhiteSpace(base32)) return Array.Empty<byte>();
        string clean = CleanSecret(base32);
        var output = new List<byte>();
        int buffer = 0;
        int bitsLeft = 0;
        foreach (char c in clean)
        {
            int val = Base32Alphabet.IndexOf(c);
            if (val < 0) continue;
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }
        return output.ToArray();
    }
}
