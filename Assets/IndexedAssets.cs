using System.Text;

namespace BetterLC;

public static class IndexedAssets
{
    public static async Task PrepareAsync(HttpClient client, string cacheRoot, string category,
        string indexUrl, string indexSha1, string baseUrl, string destination,
        CancellationToken token = default)
    {
        var indexPath = await ArtifactDownloader.DownloadAsync(client, new Uri(indexUrl), cacheRoot,
            $"downloads/{category}-index.txt", indexSha1, token);
        var lines = (await File.ReadAllLinesAsync(indexPath, Encoding.UTF8, token))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(parts => (Name: parts[0], Hash: parts[1]))
            .ToArray();
        Console.WriteLine($"Preparing {lines.Length} {category} files...");
        var done = 0;
        await Parallel.ForEachAsync(lines, new ParallelOptions
        {
            MaxDegreeOfParallelism = 12,
            CancellationToken = token
        }, async (item, cancellation) =>
        {
            await ArtifactDownloader.DownloadAsync(client, new Uri(baseUrl + item.Hash),
                destination, item.Name, item.Hash, cancellation);
            var count = Interlocked.Increment(ref done);
            if (count % 100 == 0 || count == lines.Length)
                Console.WriteLine($"{category}: {count}/{lines.Length}");
        });
    }
}
