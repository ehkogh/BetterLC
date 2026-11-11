using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterLC;

internal sealed partial class LauncherForm : Form
{
    private readonly LauncherSettings settings = LauncherSettings.Load();
    private readonly string cacheRoot;
    private AvailableVersion[] versions = [];
    private bool running;
    private string authUrl = "https://www.microsoft.com/link";
    private string authCode = "";

    private readonly ComboBox versionBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox moduleBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox serverBox = new() { Dock = DockStyle.Fill };
    private readonly ComboBox modeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private static readonly (ConnectionMode Mode, string Name)[] Modes =
    [
        (ConnectionMode.Offline, "Offline (local)"),
        (ConnectionMode.BetterLC, "BetterLC server"),
        (ConnectionMode.Lunar, "Lunar directly")
    ];
    private readonly Button playButton = new() { Text = "Launch", AutoSize = true, Enabled = false };
    private readonly Button refreshButton = new() { Text = "Refresh versions", AutoSize = true };
    private readonly Label status = new() { Text = "Starting…", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly FlowLayoutPanel authPanel = new() { AutoSize = true, Visible = false, WrapContents = false };
    private readonly Label codeLabel = new() { AutoSize = true, Font = new Font(FontFamily.GenericMonospace, 11f, FontStyle.Bold), Anchor = AnchorStyles.Left };
    private readonly Button signOutButton = new() { Text = "Sign out", AutoSize = true };
    private readonly Label progressText = new() { Text = "Checking available versions…", AutoSize = true };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Height = 18 };
    private readonly TextBox log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window
    };

    [GeneratedRegex(@"Open (https://\S+) and enter code: ([A-Z0-9]+)")]
    private static partial Regex AuthLine();
    [GeneratedRegex(@"(\d+)/(\d+)")]
    private static partial Regex CountLine();

    public LauncherForm(string cacheRoot)
    {
        this.cacheRoot = cacheRoot;
        Text = "BetterLC";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Font = SystemFonts.MessageBoxFont!;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(600, 460);
        Size = new Size(740, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();

        serverBox.Text = settings.Server;
        foreach (var mode in Modes) modeBox.Items.Add(mode.Name);
        modeBox.SelectedIndexChanged += (_, _) => ShowMode();
        modeBox.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.Mode == settings.Mode));
        versionBox.SelectedIndexChanged += (_, _) => ShowModules();
        playButton.Click += (_, _) => Launch();
        refreshButton.Click += (_, _) => RefreshVersions();
        signOutButton.Click += (_, _) => SignOut();
        Shown += (_, _) => RefreshVersions();
        FormClosing += (_, e) =>
        {
            if (!running) return;
            e.Cancel = true;
            WindowState = FormWindowState.Minimized;
        };
    }

    private void BuildLayout()
    {
        var game = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true };
        game.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        game.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(game, "Minecraft version:", versionBox);
        AddRow(game, "Loader:", moduleBox);
        AddRow(game, "Connection:", modeBox);
        AddRow(game, "BetterLC server:", serverBox);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        actions.Controls.AddRange([playButton, refreshButton]);
        game.Controls.Add(actions);
        game.SetColumnSpan(actions, 2);

        var openPage = new Button { Text = "Open sign-in page", AutoSize = true };
        openPage.Click += (_, _) => Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        var copyCode = new Button { Text = "Copy code", AutoSize = true };
        copyCode.Click += (_, _) => { if (authCode.Length > 0) Clipboard.SetText(authCode); };
        authPanel.Controls.AddRange([new Label { Text = "Sign-in code:", AutoSize = true, Anchor = AnchorStyles.Left },
            codeLabel, openPage, copyCode]);
        var account = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        account.Controls.AddRange([status, authPanel, signOutButton]);

        var activity = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill };
        activity.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        activity.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        activity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        activity.Controls.Add(progressText);
        activity.Controls.Add(progress);
        activity.Controls.Add(log);

        var root = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(Group("Game", game));
        root.Controls.Add(Group("Microsoft account", account));
        root.Controls.Add(Group("Activity", activity));
        Controls.Add(root);
    }

    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
        table.Controls.Add(control);
    }

    private static GroupBox Group(string title, Control content)
    {
        var box = new GroupBox { Text = title, Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        box.Controls.Add(content);
        return box;
    }

    public void AppendLine(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLine(line));
            return;
        }
        line = line.Trim();
        if (line.Length == 0) return;
        if (log.Lines.Length > 400) log.Lines = log.Lines[^300..];
        log.AppendText(line + Environment.NewLine);

        if (AuthLine().Match(line) is { Success: true } auth)
        {
            authUrl = auth.Groups[1].Value;
            authCode = auth.Groups[2].Value;
            codeLabel.Text = authCode;
            authPanel.Visible = true;
            SetStatus("Sign in", "Enter this code on Microsoft's sign-in page.");
            progress.Style = ProgressBarStyle.Marquee;
        }
        else if (line.StartsWith("Reusing saved Microsoft sign-in")) SetStatus("Signing in", "Using your saved Microsoft sign-in…");
        else if (line.StartsWith("Signed in as ")) { authPanel.Visible = false; SetStatus("Starting game", line); }
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
        progress.Style = ProgressBarStyle.Continuous;
        progress.Value = Math.Clamp(value, 0, 100);
    }

    private async void RefreshVersions()
    {
        if (running) return;
        refreshButton.Enabled = playButton.Enabled = false;
        progress.Style = ProgressBarStyle.Marquee;
        SetStatus("Loading versions", "Checking Lunar's current version list…");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            versions = await VersionCatalog.LoadAsync(client, Guid.NewGuid().ToString());
            versionBox.BeginUpdate();
            versionBox.Items.Clear();
            foreach (var version in versions) versionBox.Items.Add(version.Id);
            versionBox.EndUpdate();
            var index = Array.FindIndex(versions, v => v.Id == settings.Version);
            versionBox.SelectedIndex = index >= 0 ? index : 0;
            playButton.Enabled = versions.Length > 0;
            SetProgress(0);
            SetStatus("Ready", $"{versions.Length} Minecraft versions available.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                       or InvalidDataException or KeyNotFoundException)
        {
            SetProgress(0);
            SetStatus("Version check failed", "Could not load the version list. Try Refresh versions.");
            AppendLine(ex.Message);
        }
        finally { refreshButton.Enabled = true; }
    }

    private ConnectionMode SelectedMode => Modes[Math.Max(0, modeBox.SelectedIndex)].Mode;

    private void ShowMode()
    {
        serverBox.Enabled = !running && SelectedMode == ConnectionMode.BetterLC;
    }

    private void ShowModules()
    {
        moduleBox.Items.Clear();
        if (versionBox.SelectedIndex < 0) return;
        var version = versions[versionBox.SelectedIndex];
        foreach (var module in version.Modules) moduleBox.Items.Add(module.Name);
        var chosen = settings.Modules.TryGetValue(version.Id, out var saved)
            ? Array.FindIndex(version.Modules, m => m.Id == saved) : -1;
        if (chosen < 0) chosen = Math.Max(0, Array.FindIndex(version.Modules, m => m.IsDefault));
        if (moduleBox.Items.Count > 0) moduleBox.SelectedIndex = chosen;
    }

    private async void Launch()
    {
        if (running || versionBox.SelectedIndex < 0 || moduleBox.SelectedIndex < 0) return;
        var mode = SelectedMode;
        Uri? server = null;
        if (mode == ConnectionMode.BetterLC &&
            (!Uri.TryCreate(serverBox.Text.Trim(), UriKind.Absolute, out server) || server.Scheme != Uri.UriSchemeHttps))
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
        log.Clear();
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
            control.Enabled = !value;
        ShowMode();
        playButton.Enabled = !value && versions.Length > 0;
        authPanel.Visible = false;
        if (value) progress.Style = ProgressBarStyle.Marquee;
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
