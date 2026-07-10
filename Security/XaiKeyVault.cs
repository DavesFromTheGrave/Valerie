using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Valerie.Security;

/// <summary>
/// Loads multi-key xAI secrets from a revsecrets-style DPAPI vault
/// (A:\env\xai-keys.dpapi): JSON { version, scheme, encrypted: base64(DPAPI(env-text)) }.
/// </summary>
public static class XaiKeyVault
{
    public const string DefaultVaultPath = @"A:\env\xai-keys.dpapi";

    public static Dictionary<string, string> TryLoadAll(string? path = null)
    {
        path ??= DefaultVaultPath;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) return result;
            if (root.GetProperty("scheme").GetString() != "windows-dpapi-current-user") return result;

            var b64 = root.GetProperty("encrypted").GetString();
            if (string.IsNullOrWhiteSpace(b64)) return result;

            var cipher = Convert.FromBase64String(b64);
            var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
            var text = Encoding.UTF8.GetString(plain);

            foreach (var line in text.Split('\n'))
            {
                var s = line.Trim();
                if (s.Length == 0 || s.StartsWith('#') || !s.Contains('=')) continue;
                var eq = s.IndexOf('=');
                var key = s[..eq].Trim();
                var val = s[(eq + 1)..].Trim();
                if (key.Length > 0 && val.Length > 0)
                    result[key] = val;
            }
        }
        catch
        {
            // Caller decides how to surface missing keys.
        }

        return result;
    }

    /// <summary>
    /// Prefer named keys in order; also honors process env overrides of the same names.
    /// </summary>
    public static string? Resolve(params string[] preferredNames)
    {
        foreach (var name in preferredNames)
        {
            var env = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        }

        var vault = TryLoadAll();
        foreach (var name in preferredNames)
        {
            if (vault.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return null;
    }
}
