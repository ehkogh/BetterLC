using System.Net.Http.Json;
using System.Text.Json;

namespace BetterLC;

public sealed record AvailableModule(string Id, string Name, bool IsDefault);
public sealed record AvailableVersion(string Id, string Family, bool IsDefault,
    string AssetIndexUrl, string AssetIndexSha1, AvailableModule[] Modules);

public static class VersionCatalog
{
    public static async Task<AvailableVersion[]> LoadAsync(HttpClient client, string installationId,
        CancellationToken token = default)
    {
        var url = new Uri("https://api.lunarclientprod.com/launcher/metadata/versions/lunar?" +
            $"os={HostPlatform.Os}&arch={HostPlatform.Arch}&launcher_version=3.7.32-ow&installation_id={Uri.EscapeDataString(installationId)}");
        using var metadata = await client.GetFromJsonAsync<JsonDocument>(url, token)
            ?? throw new InvalidDataException("Missing Lunar version metadata.");
        var versions = new List<AvailableVersion>();
        foreach (var family in metadata.RootElement.GetProperty("versions").EnumerateArray())
        {
            var familyId = family.GetProperty("id").GetString()!;
            foreach (var item in family.GetProperty("subversions").EnumerateArray())
            {
                var modules = item.GetProperty("modules").EnumerateArray().Select(module =>
                    new AvailableModule(module.GetProperty("id").GetString()!,
                        module.GetProperty("name").GetString()!,
                        module.GetProperty("default").GetBoolean())).ToArray();
                if (modules.Length == 0) continue;
                var assets = item.GetProperty("assets");
                versions.Add(new AvailableVersion(item.GetProperty("id").GetString()!, familyId,
                    item.GetProperty("default").GetBoolean(),
                    assets.GetProperty("url").GetString()!,
                    assets.GetProperty("sha1").GetString()!, modules));
            }
        }
        return versions.OrderByDescending(x => Version.Parse(x.Id)).ToArray();
    }
}
