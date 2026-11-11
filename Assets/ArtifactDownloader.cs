using System.Security.Cryptography;

namespace BetterLC;

public static class ArtifactDownloader
{
    public static async Task<string> DownloadAsync(HttpClient client, Uri url, string cacheRoot,
        string relativeName, string expectedSha1, CancellationToken cancellationToken = default)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Artifact URL must use HTTPS.", nameof(url));
        if (expectedSha1.Length != 40 || !expectedSha1.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected a 40-digit SHA-1 hash.", nameof(expectedSha1));
        if (Path.IsPathRooted(relativeName)) throw new ArgumentException("Artifact path must be relative.", nameof(relativeName));

        var root = Path.GetFullPath(cacheRoot);
        var destination = Path.GetFullPath(Path.Combine(root, relativeName));
        if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Artifact path escapes the cache.", nameof(relativeName));

        if (File.Exists(destination) && await MatchesHashAsync(destination, expectedSha1, cancellationToken))
            return destination;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".betterlc-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, useAsync: true))
                await response.Content.CopyToAsync(output, cancellationToken);
            if (!await MatchesHashAsync(temporary, expectedSha1, cancellationToken))
                throw new InvalidDataException("Downloaded artifact failed SHA-1 verification.");
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<bool> MatchesHashAsync(string path, string expectedSha1, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, useAsync: true);
        var actual = await SHA1.HashDataAsync(stream, token);
        return Convert.ToHexString(actual).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase);
    }
}
