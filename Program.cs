using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace BetterLC;

internal static class Program
{
    private static readonly string DefaultCache = Path.Combine(HostPlatform.DataRoot, "cache");
    private static readonly Uri DefaultServer = new("https://betterlc.ehko.dev");

    [STAThread]
    private static int Main(string[] args)
    {
        PrepareDataRoot();
        if (args.Length == 0) return RunWindow();
        if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected) AttachConsole(-1);
        try { return RunCliAsync(args).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunWindow() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .With(new MacOSPlatformOptions { ShowInDock = true })
        .StartWithClassicDesktopLifetime([]);

    private static void PrepareDataRoot()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(HostPlatform.DataRoot);
        else Directory.CreateDirectory(HostPlatform.DataRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class App : Application
    {
        public override void Initialize()
        {
            Name = "BetterLC";
            RequestedThemeVariant = ThemeVariant.Default;
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new LauncherWindow(DefaultCache);
                var writer = new LineWriter(window.AppendLine);
                Console.SetOut(writer);
                Console.SetError(writer);
                desktop.MainWindow = window;
            }
            base.OnFrameworkInitializationCompleted();
        }
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        switch (args)
        {
            case ["versions-json"]:
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var versions = await VersionCatalog.LoadAsync(client, Guid.NewGuid().ToString());
                Console.WriteLine(JsonSerializer.Serialize(versions,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
                return 0;
            }

            case ["launch-version", var version, var module, .. var options]:
            {
                Uri? server = null;
                ConnectionMode? mode = null;
                string? cache = null;
                for (var i = 0; i < options.Length; i++)
                {
                    switch (options[i])
                    {
                        case "--server":
                            if (++i >= options.Length || !Uri.TryCreate(options[i], UriKind.Absolute, out server))
                                throw new ArgumentException("--server needs an absolute URL.");
                            break;
                        case "--mode":
                            if (++i >= options.Length || !Enum.TryParse<ConnectionMode>(options[i], true, out var parsed))
                                throw new ArgumentException("--mode must be betterlc, lunar or offline.");
                            mode = parsed;
                            break;
                        default:
                            if (options[i].StartsWith("--", StringComparison.Ordinal) || cache is not null)
                                throw new ArgumentException("Unexpected launch option: " + options[i]);
                            cache = options[i];
                            break;
                    }
                }
                cache ??= DefaultCache;
                mode ??= server is null ? ConnectionMode.Offline : ConnectionMode.BetterLC;
                if (mode == ConnectionMode.BetterLC) server ??= DefaultServer;
                using var gameLock = AcquireGameLock();
                var prepared = await GamePreparation.PrepareAsync(cache, version, module);
                var account = await MicrosoftAuth.SignInAsync(cache);
                await GameLauncher.LaunchAsync(prepared, account, mode.Value, server);
                return 0;
            }

            case ["sign-out", .. var rest] when rest.Length <= 1:
                CredentialCache.Delete(rest.Length == 1 ? rest[0] : DefaultCache);
                Console.WriteLine("Saved Microsoft sign-in removed.");
                return 0;

            case ["patch", var source, var destination]:
                await JarPatcher.PatchAsync(source, destination, requireOverrideHook: false, requireSentryOff: false);
                return 0;

            default:
                Console.WriteLine("""
                    BetterLC (run without arguments for the launcher window)
                      launch-version <version> <loader> [--mode offline|lunar|betterlc] [--server <url>] [cache]
                      versions-json
                      sign-out [cache]
                      patch <source.jar> <destination.jar>
                    """);
                return 1;
        }
    }

    internal static FileStream AcquireGameLock()
    {
        try { return new FileStream(Path.Combine(HostPlatform.DataRoot, "launcher.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another BetterLC game is already running."); }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
