using System.Text.Json;

namespace BetterLC;

public static class GameAssets
{
    public static async Task<string> PrepareAsync(HttpClient client, string cacheRoot, AvailableVersion version,
        CancellationToken token = default)
    {
        var indexUrl = new Uri(version.AssetIndexUrl);
        var assetIndex = Path.GetFileNameWithoutExtension(indexUrl.AbsolutePath);
        if (string.IsNullOrWhiteSpace(assetIndex) || assetIndex.Any(ch => !char.IsLetterOrDigit(ch) && ch != '.'))
            throw new InvalidDataException("Invalid Minecraft asset index name.");
        var gameRoot = Path.Combine(cacheRoot, "game");
        var indexPath = await ArtifactDownloader.DownloadAsync(client,
            indexUrl, gameRoot,
            $"assets/indexes/{assetIndex}.json", version.AssetIndexSha1, token);
        using var assetIndexDocument = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath, token));
        var assets = assetIndexDocument.RootElement.GetProperty("objects").EnumerateObject()
            .Select(item => item.Value.GetProperty("hash").GetString()!)
            .Distinct().ToArray();
        Console.WriteLine($"Preparing {assets.Length} Minecraft assets...");
        var completed = 0;
        await Parallel.ForEachAsync(assets, new ParallelOptions
        {
            MaxDegreeOfParallelism = 12,
            CancellationToken = token
        }, async (hash, cancellation) =>
        {
            var prefix = hash[..2];
            await ArtifactDownloader.DownloadAsync(client,
                new Uri($"https://resources.download.minecraft.net/{prefix}/{hash}"),
                gameRoot, $"assets/objects/{prefix}/{hash}", hash, cancellation);
            var count = Interlocked.Increment(ref completed);
            if (count % 250 == 0 || count == assets.Length)
                Console.WriteLine($"Minecraft assets: {count}/{assets.Length}");
        });
        return assetIndex;
    }
}
