using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace BetterLC;

public enum ConnectionMode
{
    BetterLC,
    Lunar,
    Offline
}

public static class GameLauncher
{
    private const int IpcPort = 28191;
    private const string Disabled = "http://127.0.0.1:9";
    private const string NoTrace = "not supplied";

    internal static async Task LaunchAsync(PreparedGame game, MinecraftSession account, ConnectionMode mode,
        Uri? server, CancellationToken token = default)
    {
        await using var api = new ApiProxy();
        var overrides = new List<string>
        {
            "-DserviceOverrideStyngr=" + Disabled,
            $"-DserviceOverrideApi=http://127.0.0.1:{api.Port}"
        };
        await using var offline = await ConfigureConnectionAsync(mode, server, game, account, overrides, token);
        if (mode != ConnectionMode.Lunar) UiPatcher.HideLunarOnlyButtons(Path.Combine(game.CacheRoot, "ui"));
        await using var ipc = GameIpc.Start(IpcPort);

        var dataDir = Path.Combine(game.CacheRoot, "lunar");
        MigrateOldSessions(game.CacheRoot, dataDir);
        var accountPath = Path.Combine(dataDir, "settings", "game", "accounts.json");
        try
        {
            await WriteAccountAsync(accountPath, account, token);
            await RunGameAsync(BuildStartInfo(game, account, overrides, dataDir, Guid.NewGuid().ToString()), account, token);
        }
        finally
        {
            try { File.Delete(accountPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Could not remove the temporary account file " + accountPath);
            }
        }
    }

    private static void MigrateOldSessions(string cacheRoot, string dataDir)
    {
        var sessions = new DirectoryInfo(Path.Combine(cacheRoot, "sessions"));
        if (!sessions.Exists) return;
        var settings = Path.Combine(dataDir, "settings");
        var newest = sessions.GetDirectories()
            .Where(session => Directory.Exists(Path.Combine(session.FullName, "settings", "game")))
            .MaxBy(session => session.LastWriteTimeUtc);
        if (newest is not null && !Directory.Exists(settings))
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(newest.FullName, "settings"), "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).Equals("accounts.json", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(settings, Path.GetRelativePath(Path.Combine(newest.FullName, "settings"), file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            Console.WriteLine("Restored Lunar settings from the last session.");
        }
        try { sessions.Delete(recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not remove old session folders in " + sessions.FullName);
        }
    }

    private static async Task<LoopbackServer?> ConfigureConnectionAsync(ConnectionMode mode, Uri? server,
        PreparedGame game, MinecraftSession account, List<string> overrides, CancellationToken token)
    {
        switch (mode)
        {
            case ConnectionMode.BetterLC:
                var socketBase = await CheckServerAsync(
                    server ?? throw new ArgumentException("BetterLC mode needs a server URL."), token);
                overrides.Add($"-DserviceOverrideAssetServer={new Uri(socketBase, "/assets")}");
                overrides.Add($"-DserviceOverrideAuthenticator={new Uri(socketBase, "/auth")}");
                return null;
            case ConnectionMode.Offline:
                var offline = new OfflineServices(game.CacheRoot, account.Uuid).Start();
                overrides.Add($"-DserviceOverrideAuthenticator=ws://127.0.0.1:{offline.Port}/auth");
                overrides.Add($"-DserviceOverrideAssetServer=ws://127.0.0.1:{offline.Port}/assets");
                Console.WriteLine("Offline mode: Lunar's websockets are answered locally.");
                return offline;
            default:
                Console.WriteLine("Lunar mode: the game talks to Lunar directly (not filtered).");
                return null;
        }
    }

    private static async Task<Uri> CheckServerAsync(Uri server, CancellationToken token)
    {
        if (server.Scheme != Uri.UriSchemeHttps && !(server.Scheme == Uri.UriSchemeHttp && server.IsLoopback))
            throw new ArgumentException("The BetterLC server must use HTTPS.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try { (await http.GetAsync(new Uri(server, "/health"), token)).EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"BetterLC server {server.Host} is not reachable ({ex.Message}).");
        }
        Console.WriteLine("Connected to BetterLC server " + server.Host);
        return new UriBuilder(server) { Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", Path = "" }.Uri;
    }

    private static async Task WriteAccountAsync(string path, MinecraftSession account, CancellationToken token)
    {
        var localId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(new
        {
            activeAccountLocalId = localId,
            accounts = new Dictionary<string, object>
            {
                [localId] = new
                {
                    accessToken = account.AccessToken,
                    accessTokenExpiresAt = account.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    eligibleForMigration = false,
                    hasMultipleProfiles = false,
                    legacy = false,
                    persistent = true,
                    localId,
                    refreshToken = "BetterLC-session-only",
                    minecraftProfile = new { id = account.Uuid, name = account.Username },
                    remoteId = account.Xuid,
                    type = "xbox",
                    username = account.Username,
                    userProperites = Array.Empty<string>()
                }
            }
        });
        await File.WriteAllTextAsync(path, json, token);
    }

    private static ProcessStartInfo BuildStartInfo(PreparedGame game, MinecraftSession account,
        List<string> overrides, string dataDir, string launchId)
    {
        var working = Path.Combine(game.CacheRoot, "multiver");
        var gameDir = Path.Combine(game.CacheRoot, "game");
        Directory.CreateDirectory(gameDir);
        var start = new ProcessStartInfo(game.JavaPath)
        {
            WorkingDirectory = working,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in game.JreArguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("-Xmx4096m");
        start.ArgumentList.Add($"-Dlunar.dataDir={dataDir}");
        foreach (var setting in overrides) start.ArgumentList.Add(setting);
        start.ArgumentList.Add("-Djava.library.path=natives");
        start.ArgumentList.Add("-cp");
        start.ArgumentList.Add(string.Join(';', game.ClassPath));
        start.ArgumentList.Add(game.MainClass);

        (string Key, string Value)[] arguments =
        [
            ("--version", game.VersionId), ("--launcherVersion", "3.7.32-ow"),
            ("--launcherFeatureFlags", "[]"), ("--installationId", game.InstallationId),
            ("--sentryTraceId", NoTrace), ("--launchId", launchId),
            ("--canaryToken", game.CanaryToken), ("--username", account.Username),
            ("--uuid", account.Uuid), ("--xuid", account.Xuid),
            ("--accessToken", account.AccessToken),
            ("--userProperties", "{}"), ("--assetIndex", game.AssetIndex),
            ("--gameDir", gameDir), ("--texturesDir", Path.Combine(game.CacheRoot, "textures")),
            ("--uiDir", Path.Combine(game.CacheRoot, "ui")),
            ("--webosrDir", Path.Combine(working, "natives")),
            ("--workingDirectory", "."), ("--classpathDir", "."),
            ("--width", "1280"), ("--height", "720"),
            ("--ipcPort", IpcPort.ToString(CultureInfo.InvariantCulture)),
            ("--ichorClassPath", string.Join(',', game.ClassPath)),
            ("--ichorExternalFiles", string.Join(',', game.ExternalFiles))
        ];
        foreach (var (key, value) in arguments)
        {
            start.ArgumentList.Add(key);
            start.ArgumentList.Add(value);
        }
        return start;
    }

    private static async Task RunGameAsync(ProcessStartInfo start, MinecraftSession account, CancellationToken token)
    {
        var recent = new Queue<string>();
        void Keep(string? line)
        {
            if (line is null) return;
            lock (recent)
            {
                if (recent.Count == 80) recent.Dequeue();
                recent.Enqueue(line);
            }
        }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Keep(e.Data);
        process.ErrorDataReceived += (_, e) => Keep(e.Data);
        if (!process.Start()) throw new InvalidOperationException("Could not start the JVM.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Console.WriteLine($"Game started (PID {process.Id}) as {account.Username}.");
        await process.WaitForExitAsync(token);
        Console.WriteLine($"Game exited with code {process.ExitCode}.");
        if (process.ExitCode == 0) return;
        lock (recent)
            foreach (var line in recent.Where(x => x.Contains("Exception") || x.Contains("ERROR") || x.Contains("Error")))
                Console.Error.WriteLine(line);
    }
}
