using System.Runtime.InteropServices;

namespace BetterLC;

internal static class HostPlatform
{
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

    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "BetterLC");
}
