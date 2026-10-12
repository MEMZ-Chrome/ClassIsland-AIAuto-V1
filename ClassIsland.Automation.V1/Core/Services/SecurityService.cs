using System.Security.Cryptography;
using System.Text;
using ClassIsland.Automation.Core.Models;

namespace ClassIsland.Automation.Core.Services;

public enum AccessAuthResult
{
    SuccessPassword,
    SuccessActiveTotp,
    Failed
}

/// <summary>
/// 负责固定密码安全散列存储与验证、TOTP 激活流控以及设置界面访问鉴权。
/// </summary>
public static class SecurityService
{
    private const int Pbkdf2Iterations = 10000;
    private const int SaltByteSize = 16;
    private const int HashByteSize = 32;

    public static bool HasPassword(PluginSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.PasswordHash);
    }

    public static bool HasActiveTotp(PluginSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.TotpSecret);
    }

    public static bool IsActiveTotpEnabled(PluginSettings settings) => HasActiveTotp(settings);

    public static bool HasPendingTotp(PluginSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.PendingTotpSecret);
    }

    /// <summary>
    /// 检查是否启用了主动防护锁定（已设置固定密码或已正式激活生效 TOTP）。
    /// 注意：待验证的暂存 TOTP 绝不能触发锁定，确保在验证可用前绝对不会意外锁死用户！
    /// </summary>
    public static bool IsActiveProtectionEnabled(PluginSettings settings)
    {
        return HasPassword(settings) || HasActiveTotp(settings);
    }

    /// <summary>
    /// 设置或清除固定密码。如果密码为空则清除。
    /// </summary>
    public static void SetPassword(PluginSettings settings, string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            settings.PasswordHash = string.Empty;
            settings.PasswordSalt = string.Empty;
            return;
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltByteSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            HashByteSize);

        settings.PasswordSalt = Convert.ToBase64String(salt);
        settings.PasswordHash = Convert.ToBase64String(hash);
    }

    /// <summary>
    /// 校验固定密码是否正确
    /// </summary>
    public static bool VerifyPassword(PluginSettings settings, string? password)
    {
        if (!HasPassword(settings))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(settings.PasswordSalt) || string.IsNullOrWhiteSpace(settings.PasswordHash))
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(settings.PasswordSalt);
            byte[] expectedHash = Convert.FromBase64String(settings.PasswordHash);

            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256,
                HashByteSize);

            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 发起生成或重新配置 TOTP：仅保存到 PendingTotpSecret 暂存区，在验证可用前绝不生效
    /// </summary>
    public static string StartSetupTotp(PluginSettings settings)
    {
        string secret = TotpService.GenerateSecret();
        settings.PendingTotpSecret = secret;
        return secret;
    }

    /// <summary>
    /// 验证并正式激活暂存的 TOTP。只有此处验证 6 位验证码完全正确后，TOTP 才会正式成为生效的保护机制。
    /// </summary>
    public static bool VerifyAndActivatePendingTotp(PluginSettings settings, string inputCode)
    {
        if (string.IsNullOrWhiteSpace(settings.PendingTotpSecret))
        {
            return false;
        }

        if (TotpService.VerifyTotp(settings.PendingTotpSecret, inputCode))
        {
            // 验证通过，正式生效！
            settings.TotpSecret = settings.PendingTotpSecret;
            settings.PendingTotpSecret = string.Empty;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 取消或放弃暂存中的待验证 TOTP
    /// </summary>
    public static void CancelPendingTotp(PluginSettings settings)
    {
        settings.PendingTotpSecret = string.Empty;
    }

    /// <summary>
    /// 完全关闭 TOTP 保护
    /// </summary>
    public static void DisableTotp(PluginSettings settings)
    {
        settings.TotpSecret = string.Empty;
        settings.PendingTotpSecret = string.Empty;
    }

    /// <summary>
    /// 校验用户输入的密码或 6 位 TOTP 验证码是否能够解锁设置页面
    /// </summary>
    public static AccessAuthResult VerifyAccess(PluginSettings settings, string? input)
    {
        if (!IsActiveProtectionEnabled(settings))
        {
            return AccessAuthResult.SuccessPassword;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return AccessAuthResult.Failed;
        }

        input = input.Trim();

        // 1. 若配置了固定密码，优先比对固定密码
        if (HasPassword(settings) && VerifyPassword(settings, input))
        {
            return AccessAuthResult.SuccessPassword;
        }

        // 2. 若激活了 TOTP 且输入为 6 位数字，比对活跃 TOTP
        if (HasActiveTotp(settings) && input.Length == 6 && input.All(char.IsDigit))
        {
            if (TotpService.VerifyTotp(settings.TotpSecret, input))
            {
                return AccessAuthResult.SuccessActiveTotp;
            }
        }

        return AccessAuthResult.Failed;
    }
}
