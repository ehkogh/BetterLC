using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace BetterLC;

internal sealed partial class LauncherWindow : Window
{
    private readonly LauncherSettings settings = LauncherSettings.Load();
    private readonly string cacheRoot;
    private AvailableVersion[] versions = [];
    private bool running;
    private string authUrl = "https://www.microsoft.com/link";
    private string authCode = "";
    private readonly List<string> lines = [];

    private static readonly (ConnectionMode Mode, string Name)[] Modes =
    [
        (ConnectionMode.Offline, "Offline (local)"),
        (ConnectionMode.BetterLC, "BetterLC server"),
        (ConnectionMode.Lunar, "Lunar directly")
    ];
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    private readonly ComboBox versionBox = Stretch(new ComboBox());
    private readonly ComboBox moduleBox = Stretch(new ComboBox());
    private readonly ComboBox modeBox = Stretch(new ComboBox { ItemsSource = Modes.Select(m => m.Name).ToArray() });
    private readonly TextBox serverBox = new();
    private readonly Button playButton = new() { Content = "Launch", IsEnabled = false, Classes = { "accent" } };
    private readonly Button refreshButton = new() { Content = "Refresh versions" };
    private readonly TextBlock status = new() { Text = "Starting…", FontWeight = FontWeight.Bold };
    private readonly StackPanel authPanel = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private readonly TextBlock codeLabel = new() { FontFamily = Mono, FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button signOutButton = new() { Content = "Sign out" };
    private readonly TextBlock progressText = new() { Text = "Checking available versions…", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Height = 18, Minimum = 0, Maximum = 100 };
    private readonly TextBox log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = Mono, FontSize = 12
    };

    [GeneratedRegex(@"Open (https://\S+) and enter code: ([A-Z0-9]+)")]
    private static partial Regex AuthLine();
    [GeneratedRegex(@"(\d+)/(\d+)")]
    private static partial Regex CountLine();

    public LauncherWindow(string cacheRoot)
    {
        this.cacheRoot = cacheRoot;
        Title = "BetterLC";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://BetterLC/Assets/logo.png")));
        MinWidth = 600;
        MinHeight = 480;
        Width = 740;
        Height = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildLayout();

        serverBox.Text = settings.Server;
        modeBox.SelectionChanged += (_, _) => ShowMode();
        modeBox.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.Mode == settings.Mode));
        versionBox.SelectionChanged += (_, _) => ShowModules();
        playButton.Click += (_, _) => Launch();
        refreshButton.Click += (_, _) => RefreshVersions();
        signOutButton.Click += (_, _) => SignOut();
        Opened += (_, _) => RefreshVersions();
        Closing += (_, e) =>
        {
            if (!running) return;
            e.Cancel = true;
            WindowState = WindowState.Minimized;
        };
    }

    private static T Stretch<T>(T control) where T : Control
    {
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        return control;
    }

    private Control BuildLayout()
    {
        var game = new Grid { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto,Auto,Auto,Auto"), RowSpacing = 8, ColumnSpacing = 12 };
        AddRow(game, 0, "Minecraft version", versionBox);
        AddRow(game, 1, "Loader", moduleBox);
        AddRow(game, 2, "Connection", modeBox);
        AddRow(game, 3, "BetterLC server", serverBox);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { playButton, refreshButton } };
        Grid.SetRow(actions, 4);
        Grid.SetColumnSpan(actions, 2);
        game.Children.Add(actions);

        var openPage = new Button { Content = "Open sign-in page" };
        openPage.Click += (_, _) => Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        var copyCode = new Button { Content = "Copy code" };
        copyCode.Click += async (_, _) =>
        {
            if (authCode.Length > 0 && Clipboard is { } clipboard) await clipboard.SetTextAsync(authCode);
        };
        authPanel.Children.AddRange([new TextBlock { Text = "Sign-in code:", VerticalAlignment = VerticalAlignment.Center },
            codeLabel, openPage, copyCode]);
        var account = new StackPanel { Spacing = 8, Children = { status, authPanel, signOutButton } };

        var activity = new DockPanel();
        DockPanel.SetDock(progressText, Dock.Top);
        DockPanel.SetDock(progress, Dock.Top);
        progress.Margin = new Thickness(0, 6, 0, 8);
        activity.Children.AddRange([progressText, progress, log]);

        var root = new DockPanel { Margin = new Thickness(12) };
        var gameGroup = Group("Game", game);
        var accountGroup = Group("Microsoft account", account);
        DockPanel.SetDock(gameGroup, Dock.Top);
        DockPanel.SetDock(accountGroup, Dock.Top);
        root.Children.AddRange([gameGroup, accountGroup, Group("Activity", activity)]);
        return root;
    }

    private static void AddRow(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.AddRange([text, control]);
    }

    private static Control Group(string title, Control content) => new Border
    {
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12),
        Margin = new Thickness(0, 0, 0, 10),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)),
        Child = new DockPanel
        {
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8), [DockPanel.DockProperty] = Dock.Top },
                content
            }
        }
    };

    public void AppendLine(string line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendLine(line));
            return;
        }
        line = line.Trim();
        if (line.Length == 0) return;
        lines.Add(line);
        if (lines.Count > 400) lines.RemoveRange(0, lines.Count - 300);
        log.Text = string.Join(Environment.NewLine, lines);
        log.CaretIndex = log.Text.Length;

        if (AuthLine().Match(line) is { Success: true } auth)
        {
            authUrl = auth.Groups[1].Value;
            authCode = auth.Groups[2].Value;
            codeLabel.Text = authCode;
            authPanel.IsVisible = true;
            SetStatus("Sign in", "Enter this code on Microsoft's sign-in page.");
            progress.IsIndeterminate = true;
        }
        else if (line.StartsWith("Reusing saved Microsoft sign-in")) SetStatus("Signing in", "Using your saved Microsoft sign-in…");
        else if (line.StartsWith("Signed in as ")) { authPanel.IsVisible = false; SetStatus("Starting game", line); }
        else if (line.StartsWith("Connected to BetterLC server") || line.StartsWith("Offline mode") ||
                 line.StartsWith("Lunar mode")) SetStatus("Starting game", line);
        else if (line.StartsWith("Game started")) { SetProgress(100); SetStatus("Game running", line); }
        else if (line.StartsWith("Game files prepared")) { SetProgress(96); SetStatus("Starting game", "Game files are ready."); }
        else if (line.StartsWith("Patched ")) SetStatus("Preparing", line);
        else if (line.StartsWith("Preparing ")) SetStatus("Preparing", line);
        else if ((line.StartsWith("Minecraft assets:") || line.StartsWith("textures:") || line.StartsWith("ui:")) &&
                 CountLine().Match(line) is { Success: true } count && int.Parse(count.Groups[2].Value) > 0)
        {
            var (start, width) = line[0] == 'M' ? (60, 35) : line[0] == 't' ? (48, 12) : (40, 8);
            SetProgress(start + width * int.Parse(count.Groups[1].Value) / int.Parse(count.Groups[2].Value));
            progressText.Text = line;
        }
    }

    private void SetStatus(string label, string detail)
    {
        status.Text = label;
        progressText.Text = detail;
    }

    private void SetProgress(int value)
    {
        progress.IsIndeterminate = false;
        progress.Value = Math.Clamp(value, 0, 100);
    }

    private async void RefreshVersions()
    {
        if (running) return;
        refreshButton.IsEnabled = playButton.IsEnabled = false;
        progress.IsIndeterminate = true;
        SetStatus("Loading versions", "Checking Lunar's current version list…");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            versions = await VersionCatalog.LoadAsync(client, Guid.NewGuid().ToString());
            versionBox.ItemsSource = versions.Select(v => v.Id).ToArray();
            var index = Array.FindIndex(versions, v => v.Id == settings.Version);
            versionBox.SelectedIndex = index >= 0 ? index : 0;
            playButton.IsEnabled = versions.Length > 0;
            SetProgress(0);
            SetStatus("Ready", $"{versions.Length} Minecraft versions available.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                       or InvalidDataException or KeyNotFoundException or PlatformNotSupportedException)
        {
            SetProgress(0);
            SetStatus("Version check failed", "Could not load the version list. Try Refresh versions.");
            AppendLine(ex.Message);
        }
        finally { refreshButton.IsEnabled = true; }
    }

    private ConnectionMode SelectedMode => Modes[Math.Max(0, modeBox.SelectedIndex)].Mode;

    private void ShowMode()
    {
        serverBox.IsEnabled = !running && SelectedMode == ConnectionMode.BetterLC;
    }

    private void ShowModules()
    {
        moduleBox.ItemsSource = null;
        if (versionBox.SelectedIndex < 0) return;
        var version = versions[versionBox.SelectedIndex];
        moduleBox.ItemsSource = version.Modules.Select(m => m.Name).ToArray();
        var chosen = settings.Modules.TryGetValue(version.Id, out var saved)
            ? Array.FindIndex(version.Modules, m => m.Id == saved) : -1;
        if (chosen < 0) chosen = Math.Max(0, Array.FindIndex(version.Modules, m => m.IsDefault));
        if (version.Modules.Length > 0) moduleBox.SelectedIndex = chosen;
    }

    private async void Launch()
    {
        if (running || versionBox.SelectedIndex < 0 || moduleBox.SelectedIndex < 0) return;
        var mode = SelectedMode;
        Uri? server = null;
        if (mode == ConnectionMode.BetterLC &&
            (!Uri.TryCreate(serverBox.Text?.Trim(), UriKind.Absolute, out server) || server.Scheme != Uri.UriSchemeHttps))
        {
            SetStatus("BetterLC server", "Enter an HTTPS server address.");
            return;
        }
        var version = versions[versionBox.SelectedIndex];
        var module = version.Modules[moduleBox.SelectedIndex];
        settings.Version = version.Id;
        settings.Modules[version.Id] = module.Id;
        settings.Mode = mode;
        if (server is not null) settings.Server = server.ToString();
        settings.Save();

        SetRunning(true);
        lines.Clear();
        log.Text = "";
        SetStatus("Preparing", $"Preparing Minecraft {version.Id} ({module.Name})…");
        try
        {
            await Task.Run(async () =>
            {
                using var gameLock = Program.AcquireGameLock();
                var prepared = await GamePreparation.PrepareAsync(cacheRoot, version.Id, module.Id);
                var account = await MicrosoftAuth.SignInAsync(cacheRoot);
                await GameLauncher.LaunchAsync(prepared, account, mode, server);
            });
            SetStatus("Ready", "Game closed. Ready for another launch.");
        }
        catch (Exception ex)
        {
            AppendLine(ex.Message);
            SetStatus("Launch failed", ex.Message);
        }
        finally
        {
            SetRunning(false);
            SetProgress(0);
        }
    }

    private void SetRunning(bool value)
    {
        running = value;
        foreach (var control in new Control[] { versionBox, moduleBox, modeBox, refreshButton, signOutButton })
            control.IsEnabled = !value;
        ShowMode();
        playButton.IsEnabled = !value && versions.Length > 0;
        authPanel.IsVisible = false;
        if (value) progress.IsIndeterminate = true;
    }

    private void SignOut()
    {
        if (running) return;
        try
        {
            CredentialCache.Delete(cacheRoot);
            SetStatus("Signed out", "Saved Microsoft sign-in removed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Sign-out failed", ex.Message);
        }
    }
}

internal sealed class LineWriter(Action<string> onLine) : TextWriter
{
    private readonly StringBuilder pending = new();
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (pending)
        {
            if (value == '\n')
            {
                onLine(pending.ToString());
                pending.Clear();
            }
            else if (value != '\r') pending.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        foreach (var ch in value) Write(ch);
    }
}
