using System.Security.Cryptography;
using System.Text;

namespace Valerie.Security;

/// <summary>
/// At-rest protection for secrets (the xAI TTS key) using Windows DPAPI, scoped to the current
/// Windows user. The plaintext is never written to disk — only the DPAPI ciphertext (base64).
/// Decryption succeeds only for the same Windows user on the same machine. This is the project's
/// standard secret-handling path now that the BitLocker (B:) drive is no longer used.
/// </summary>
public static class SecretStore
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Encrypt <paramref name="plaintext"/> with DPAPI (CurrentUser) and write the
    /// base64 ciphertext to <paramref name="path"/>.</summary>
    public static void Save(string path, string plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI secret storage requires Windows.");

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
        File.WriteAllText(path, Convert.ToBase64String(cipher));
    }

    /// <summary>Read and DPAPI-decrypt the secret at <paramref name="path"/>, or null if it's
    /// missing, unreadable, or was protected by a different user/machine.</summary>
    public static string? TryLoad(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (!File.Exists(path)) return null;
            var cipher = Convert.FromBase64String(File.ReadAllText(path).Trim());
            var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
