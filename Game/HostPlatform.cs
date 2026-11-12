using System.Runtime.InteropServices;

namespace BetterLC;

internal static class HostPlatform
{
    private const string PortableMarker = "portable";
    private const string PortableFolder = "BetterLC Data";
    private static readonly Lazy<(string Root, bool Portable, string? Warning)> Data = new(ResolveDataRoot);

    public static string Os =>
        OperatingSystem.IsWindows() ? "win32" :
        OperatingSystem.IsMacOS() ? "darwin" :
        OperatingSystem.IsLinux() ? "linux" :
        throw new PlatformNotSupportedException("BetterLC runs on Windows, macOS and Linux.");

    public static string Arch => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        var other => throw new PlatformNotSupportedException($"Lunar Client doesn't support {other} processors.")
    };

    public static string DataRoot => Data.Value.Root;
    public static bool IsPortable => Data.Value.Portable;
    public static string? DataWarning => Data.Value.Warning;

    private static string InstalledRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "BetterLC");

    private static (string, bool, string?) ResolveDataRoot()
    {
        if (PortableHome() is not { } home) return Create(InstalledRoot, false, null);
        if (home.Contains("/AppTranslocation/", StringComparison.Ordinal))
            return Create(InstalledRoot, false,
                "macOS is running BetterLC from a temporary copy, so portable data can't be kept next to it. " +
                "Move the BetterLC folder somewhere else (for example your Desktop) and open it again. Until then, data goes to " + InstalledRoot);
        try { return Create(Path.Combine(home, PortableFolder), true, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Create(InstalledRoot, false, $"Can't write portable data next to BetterLC ({ex.Message}). Using {InstalledRoot} instead.");
        }
    }

    private static string? PortableHome()
    {
        var executable = Environment.ProcessPath;
        if (executable is null) return null;
        var directory = Path.GetDirectoryName(executable)!;
        if (OperatingSystem.IsMacOS() && Path.GetFileName(directory) == "MacOS" &&
            Directory.GetParent(directory) is { Name: "Contents" } contents && contents.Parent is { } bundle &&
            bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return File.Exists(Path.Combine(contents.FullName, "Resources", PortableMarker)) ? bundle.Parent?.FullName : null;
        return File.Exists(Path.Combine(directory, PortableMarker)) ? directory : null;
    }

    private static (string, bool, string?) Create(string root, bool portable, string? warning)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (root, portable, warning);
    }
}
