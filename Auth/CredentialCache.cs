using System.Security.Cryptography;
using System.Text;

namespace BetterLC;

internal static class CredentialCache
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BetterLC Microsoft refresh token v1");
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static string PathFor(string cacheRoot) => Path.Combine(Path.GetFullPath(cacheRoot), "credentials",
        OperatingSystem.IsWindows() ? "microsoft-refresh.dpapi" : "microsoft-refresh.token");

    public static async Task<string?> LoadAsync(string cacheRoot, CancellationToken token = default)
    {
        var path = PathFor(cacheRoot);
        if (!File.Exists(path)) return null;
        var stored = await File.ReadAllBytesAsync(path, token);
        if (!OperatingSystem.IsWindows()) return Encoding.UTF8.GetString(stored);
        try
        {
            var plaintext = ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
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
        var directory = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var plaintext = Encoding.UTF8.GetBytes(refreshToken);
        byte[] stored;
        try { stored = OperatingSystem.IsWindows() ? ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser) : plaintext.ToArray(); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
            await using (var file = new FileStream(temporary, options))
                await file.WriteAsync(stored, token);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (!OperatingSystem.IsWindows()) CryptographicOperations.ZeroMemory(stored);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Delete(string cacheRoot)
    {
        var path = PathFor(cacheRoot);
        if (File.Exists(path)) File.Delete(path);
    }
}
