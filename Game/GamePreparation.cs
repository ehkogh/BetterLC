using System.Net.Http.Json;
using System.Text.Json;
using System.Formats.Tar;
using System.IO.Compression;

namespace BetterLC;

public static class GamePreparation
{
    private static readonly Uri LaunchEndpoint = new("https://api.lunarclientprod.com/launcher/launch");

    public static async Task<PreparedGame> PrepareAsync(string cacheRoot, string versionId,
        string moduleId, CancellationToken token = default)
    {
        cacheRoot = Path.GetFullPath(cacheRoot);
        Directory.CreateDirectory(cacheRoot);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var installationId = Guid.NewGuid().ToString();
        var versions = await VersionCatalog.LoadAsync(client, installationId, token);
        var selected = versions.SingleOrDefault(x => x.Id == versionId)
            ?? throw new ArgumentException($"Lunar does not list Minecraft {versionId}.");
        if (!selected.Modules.Any(x => x.Id == moduleId))
            throw new ArgumentException($"Lunar does not list {moduleId} for Minecraft {versionId}.");
        var request = new
        {
            installation_id = installationId,
            os = HostPlatform.Os,
            os_release = "unknown",
            arch = HostPlatform.Arch,
            launcher_version = "3.7.32-ow",
            version = versionId,
            branch = "master",
            args = Array.Empty<string>(),
            module = moduleId,
            profile = new { id = Guid.NewGuid().ToString(), name = "BetterLC" }
        };
        using var response = await client.PostAsJsonAsync(LaunchEndpoint, request, token);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var root = json.RootElement;
        if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
            throw new InvalidDataException(root.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text
                ? text : "Lunar did not return a usable launch manifest.");
        var artifacts = root.GetProperty("launchTypeData").GetProperty("artifacts")
            .EnumerateArray().Select(x => new Artifact(
                x.GetProperty("name").GetString()!,
                new Uri(x.GetProperty("url").GetString()!),
                x.GetProperty("sha1").GetString()!,
                x.GetProperty("type").GetString()!)).ToArray();
        var mainClass = root.GetProperty("launchTypeData").GetProperty("mainClass").GetString()!;
        var canaryToken = root.GetProperty("canaryToken").GetString() ?? "";
        Console.WriteLine($"Preparing {artifacts.Length} verified game files...");

        var fileRoot = Path.Combine(cacheRoot, "multiver");
        var done = 0;
        await Parallel.ForEachAsync(artifacts, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = token
        }, async (artifact, cancellation) =>
        {
            if (NeedsPatch(artifact.Name))
                await ArtifactDownloader.DownloadAsync(client, artifact.Url,
                    Path.Combine(cacheRoot, "original"), OriginalName(artifact), artifact.Sha1, cancellation);
            else
                await ArtifactDownloader.DownloadAsync(client, artifact.Url, fileRoot,
                    artifact.Name, artifact.Sha1, cancellation);
            var count = Interlocked.Increment(ref done);
            Console.WriteLine($"{count}/{artifacts.Length} {artifact.Name}");
        });

        if (!artifacts.Any(x => x.Name == "lunar.jar"))
            throw new InvalidDataException("Lunar's launch manifest has no lunar.jar.");
        foreach (var artifact in artifacts.Where(x => NeedsPatch(x.Name)))
        {
            var patched = Path.Combine(cacheRoot, "patched",
                Path.GetFileNameWithoutExtension(OriginalName(artifact)) + $"-p{JarPatcher.Version}.jar");
            if (!File.Exists(patched))
                await JarPatcher.PatchAsync(Path.Combine(cacheRoot, "original", OriginalName(artifact)), patched,
                    requireOverrideHook: artifact.Name == "lunar.jar", requireSentryOff: true, token);
            File.Copy(patched, Path.Combine(fileRoot, artifact.Name), overwrite: true);
        }

        var nativesRoot = Path.Combine(fileRoot, "natives");
        Directory.CreateDirectory(nativesRoot);
        foreach (var native in artifacts.Where(x => x.Type == "NATIVES"))
            ZipFile.ExtractToDirectory(Path.Combine(fileRoot, native.Name), nativesRoot, overwriteFiles: true);

        var ui = root.GetProperty("ui");
        var uiZip = await ArtifactDownloader.DownloadAsync(client,
            new Uri(ui.GetProperty("sourceUrl").GetString()!), cacheRoot,
            "downloads/ui-source.zip", ui.GetProperty("sourceSha1").GetString()!, token);
        ZipFile.ExtractToDirectory(uiZip, Path.Combine(cacheRoot, "ui"), overwriteFiles: true);
        var uiAssets = ui.GetProperty("assets");
        await IndexedAssets.PrepareAsync(client, cacheRoot, "ui",
            uiAssets.GetProperty("indexUrl").GetString()!,
            uiAssets.GetProperty("indexSha1").GetString()!,
            uiAssets.GetProperty("baseUrl").GetString()!,
            Path.Combine(cacheRoot, "ui", "assets"), token);

        var textures = root.GetProperty("textures");
        await IndexedAssets.PrepareAsync(client, cacheRoot, "textures",
            textures.GetProperty("indexUrl").GetString()!,
            textures.GetProperty("indexSha1").GetString()!,
            textures.GetProperty("baseUrl").GetString()!,
            Path.Combine(cacheRoot, "textures"), token);

        var jre = root.GetProperty("jre");
        var javaPath = JavaPath(Path.Combine(cacheRoot, "jre"),
            jre.GetProperty("executablePathInArchive").EnumerateArray().Select(x => x.GetString()!));
        if (!File.Exists(javaPath))
        {
            var download = jre.GetProperty("download");
            var extension = download.TryGetProperty("extension", out var ext) ? ext.GetString()! : "zip";
            var archive = Path.Combine(cacheRoot, "downloads", "jre." + extension);
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            var temp = archive + ".tmp";
            try
            {
                using var jreResponse = await client.GetAsync(new Uri(download.GetProperty("url").GetString()!),
                    HttpCompletionOption.ResponseHeadersRead, token);
                jreResponse.EnsureSuccessStatusCode();
                await using (var output = File.Create(temp))
                    await jreResponse.Content.CopyToAsync(output, token);
                File.Move(temp, archive, overwrite: true);
                await ExtractAsync(archive, extension, Path.Combine(cacheRoot, "jre"), token);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        if (!File.Exists(javaPath)) throw new InvalidDataException("JRE archive did not contain the expected executable.");
        var assetIndex = await GameAssets.PrepareAsync(client, cacheRoot, selected, token);
        Console.WriteLine("Game files prepared.");
        Console.WriteLine("Java runtime: " + javaPath);
        return new PreparedGame(cacheRoot, javaPath, mainClass, canaryToken, installationId,
            versionId, moduleId, assetIndex,
            jre.GetProperty("extraArguments").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            artifacts.Where(x => x.Type == "CLASS_PATH").Select(x => x.Name).ToArray(),
            artifacts.Where(x => x.Type == "EXTERNAL_FILE").Select(x => x.Name).ToArray());
    }

    private static string JavaPath(string jreRoot, IEnumerable<string> parts)
    {
        var path = Path.Combine([jreRoot, .. parts]);
        return OperatingSystem.IsWindows() && Path.GetFileName(path).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(path)!, "java.exe") : path;
    }

    private static async Task ExtractAsync(string archive, string extension, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        if (extension == "zip")
        {
            ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
            return;
        }
        if (extension is not ("tar.gz" or "tgz"))
            throw new InvalidDataException($"Lunar sent a Java runtime as .{extension}, which BetterLC can't unpack.");
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: true, token);
    }

    private sealed record Artifact(string Name, Uri Url, string Sha1, string Type);

    private static bool NeedsPatch(string name) =>
        name == "lunar.jar" || (name.StartsWith("genesis-", StringComparison.Ordinal) && name.EndsWith(".jar", StringComparison.Ordinal));

    private static string OriginalName(Artifact artifact) =>
        $"{Path.GetFileNameWithoutExtension(artifact.Name)}-{artifact.Sha1}.jar";
}

public sealed record PreparedGame(string CacheRoot, string JavaPath, string MainClass,
    string CanaryToken, string InstallationId, string VersionId, string ModuleId,
    string AssetIndex, string[] JreArguments,
    string[] ClassPath, string[] ExternalFiles);
