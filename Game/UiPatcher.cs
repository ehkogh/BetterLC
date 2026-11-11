using System.Text.RegularExpressions;

namespace BetterLC;

internal static partial class UiPatcher
{
    [GeneratedRegex(@"(\w)=>\{(\w)\(\1\.buttons\)\}")]
    private static partial Regex HomeButtons();

    [GeneratedRegex(@"(\w)\.jsxs\((\w),\{active:(\w)\.activeTab===""discover""")]
    private static partial Regex DiscoverTab();

    public static void HideLunarOnlyButtons(string uiDir)
    {
        var mainMenu = Patch(Path.Combine(uiDir, "static", "index.js"), HomeButtons(),
            "$1=>{$2($1.buttons.filter(b=>b.id!==\"gui.components.discover\"&&b.id!==\"settings.labels.store\"))}");
        var multiplayer = Patch(Path.Combine(uiDir, "static", "translations.js"), DiscoverTab(),
            "false&&$1.jsxs($2,{active:$3.activeTab===\"discover\"");
        if (mainMenu && multiplayer) Console.WriteLine("Hid Lunar's Discover and Store buttons.");
        else Console.Error.WriteLine("Couldn't find every Discover/Store button in this Lunar menu build; some may still show.");
    }

    private static bool Patch(string file, Regex pattern, string replacement)
    {
        if (!File.Exists(file)) return false;
        var text = File.ReadAllText(file);
        if (pattern.Count(text) != 1) return false;
        File.WriteAllText(file, pattern.Replace(text, replacement));
        return true;
    }
}
