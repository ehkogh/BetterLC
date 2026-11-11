using System.Security.Cryptography;
using System.Text;

namespace BetterLC;

internal static class CredentialCache
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BetterLC Microsoft refresh token v1");

    private static string PathFor(string cacheRoot) =>
        Path.Combine(Path.GetFullPath(cacheRoot), "credentials", "microsoft-refresh.dpapi");

    public static async Task<string?> LoadAsync(string cacheRoot, CancellationToken token = default)
    {
        var path = PathFor(cacheRoot);
        if (!File.Exists(path)) return null;
        try
        {
            var encrypted = await File.ReadAllBytesAsync(path, token);
            var plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (CryptographicException)
        {
            Console.WriteLine("Saved sign-in could not be opened; a new sign-in is needed.");
            return null;
        }
    }

    public static async Task SaveAsync(string cacheRoot, string refreshToken, CancellationToken token = default)
    {
        var path = PathFor(cacheRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plaintext = Encoding.UTF8.GetBytes(refreshToken);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Delete(string cacheRoot)
    {
        var path = PathFor(cacheRoot);
        if (File.Exists(path)) File.Delete(path);
    }
}
