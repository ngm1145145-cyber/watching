using System;
using System.Security.Cryptography;

namespace Watching.Common;

/// <summary>
/// 设置窗口的密码门禁。
///
/// 规则（按需求）：
///  * 从未设置过密码 —— 可以直接进入设置（首次进入时引导设置密码）。
///  * 已经设置过密码 —— 必须输入正确密码才能进入设置。
/// </summary>
public static class PasswordGate
{
    private const int Iterations = 120_000;

    public static bool HasPassword => AppConfig.Load().HasPassword;

    public static void SetPassword(string password)
    {
        var cfg = AppConfig.Load();
        if (string.IsNullOrEmpty(password))
        {
            cfg.PasswordHash = null;
            cfg.PasswordSalt = null;
        }
        else
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            cfg.PasswordSalt = Convert.ToBase64String(salt);
            cfg.PasswordHash = Convert.ToBase64String(Hash(password, salt));
        }
        cfg.Save();
    }

    public static bool Verify(string password)
    {
        var cfg = AppConfig.Load();
        if (!cfg.HasPassword) return true;
        try
        {
            var salt = Convert.FromBase64String(cfg.PasswordSalt);
            var expected = Convert.FromBase64String(cfg.PasswordHash);
            var actual = Hash(password ?? string.Empty, salt);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
}
