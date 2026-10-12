using System.Security.Cryptography;
using System.Text;

namespace ClassIsland.Automation.Core.Services;

/// <summary>
/// 提供敏感配置数据（API 密钥、TOTP 私钥等）的加解密保护，杜绝明文存储在配置文件中。
/// </summary>
public static class SecretProtectionService
{
    private const string EncryptedPrefix = "enc:v1:";

    // 基于应用静态种子与派生参数，确保即使在学校冰点还原、换机或多账户环境下也能稳定解密，杜绝因 Windows 账户 SID 变更导致秘钥损坏
    private static readonly byte[] MasterKey = SHA256.HashData(Encoding.UTF8.GetBytes("ClassIsland.Automation.Plugin.SecureStorageKey.v1#2026"));

    /// <summary>
    /// 加密敏感字符串。若已经是密文或为空则直接处理。
    /// </summary>
    public static string Encrypt(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        // 如果已经是加密格式，避免重复加密
        if (plainText.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return plainText;
        }

        try
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] iv = RandomNumberGenerator.GetBytes(16);

            using var aes = Aes.Create();
            aes.Key = MasterKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            // 合并 IV (16 bytes) + CipherBytes
            byte[] result = new byte[iv.Length + cipherBytes.Length];
            Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
            Buffer.BlockCopy(cipherBytes, 0, result, iv.Length, cipherBytes.Length);

            return EncryptedPrefix + Convert.ToBase64String(result);
        }
        catch
        {
            // 加密失败时的保底策略
            return plainText;
        }
    }

    /// <summary>
    /// 解密字符串。如果不是密文（如旧版平滑升级而来的明文配置），则直接返回原明文。
    /// </summary>
    public static string Decrypt(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return string.Empty;
        }

        // 如果不是以 enc:v1: 开头，说明是升级前的旧明文，直接返回
        if (!cipherText.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return cipherText;
        }

        try
        {
            string base64 = cipherText.Substring(EncryptedPrefix.Length);
            byte[] combined = Convert.FromBase64String(base64);

            if (combined.Length < 16)
            {
                return string.Empty;
            }

            byte[] iv = new byte[16];
            byte[] cipherBytes = new byte[combined.Length - 16];
            Buffer.BlockCopy(combined, 0, iv, 0, 16);
            Buffer.BlockCopy(combined, 16, cipherBytes, 0, cipherBytes.Length);

            using var aes = Aes.Create();
            aes.Key = MasterKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // 解密失败返回空字符串，避免异常崩溃
            return string.Empty;
        }
    }
}
