using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BetterLC;

internal sealed record MinecraftSession(string Username, string Uuid, string Xuid,
    string AccessToken, DateTimeOffset ExpiresAt);

internal static class MicrosoftAuth
{
    private sealed record MicrosoftTokens(string AccessToken, string RefreshToken);
    private const string LunarClientId = "4358653d-21f6-4697-96bb-7963ff974196";
    private const string Scope = "XboxLive.signin offline_access";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<MinecraftSession> SignInAsync(string cacheRoot, CancellationToken token = default)
    {
        MicrosoftTokens? tokens = null;
        var cached = await CredentialCache.LoadAsync(cacheRoot, token);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            Console.WriteLine("Reusing saved Microsoft sign-in...");
            tokens = await RefreshAsync(cached, token);
            if (tokens is null)
            {
                CredentialCache.Delete(cacheRoot);
                Console.WriteLine("Saved sign-in expired; a new one-time code is needed.");
            }
        }
        tokens ??= await DeviceCodeAsync(token);
        var microsoftToken = tokens.AccessToken;

        using var xbox = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + microsoftToken },
            RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT"
        }, token);
        var userToken = Required(xbox.RootElement, "Token");
        using var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT",
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { userToken } }
        }, token);
        var userHash = Required(xsts.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0], "uhs");
        var xstsToken = Required(xsts.RootElement, "Token");
        using var minecraft = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" }, token);
        var minecraftToken = Required(minecraft.RootElement, "access_token");
        var xuid = GetJwtClaim(minecraftToken, "xuid");
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(GetJwtClaim(minecraftToken, "exp")));

        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profileRequest.Headers.Authorization = new("Bearer", minecraftToken);
        using var profileResponse = await Http.SendAsync(profileRequest, token);
        if (profileResponse.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("This Microsoft account has no Minecraft: Java Edition profile.");
        profileResponse.EnsureSuccessStatusCode();
        using var profile = await JsonDocument.ParseAsync(await profileResponse.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var username = Required(profile.RootElement, "name");
        var uuid = Required(profile.RootElement, "id");
        try
        {
            await CredentialCache.SaveAsync(cacheRoot, tokens.RefreshToken, token);
            Console.WriteLine("Microsoft sign-in saved for future launches (protected by your Windows account).");
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not save sign-in for the next launch.");
        }
        Console.WriteLine($"Signed in as {username}.");
        return new MinecraftSession(username, uuid, xuid, minecraftToken, expiresAt);
    }

    private static async Task<MicrosoftTokens?> RefreshAsync(string refreshToken, CancellationToken token)
    {
        using var response = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = LunarClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = Scope
            }), token);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        if (response.IsSuccessStatusCode)
            return new MicrosoftTokens(Required(json.RootElement, "access_token"),
                json.RootElement.TryGetProperty("refresh_token", out var fresh) && fresh.ValueKind == JsonValueKind.String
                    ? fresh.GetString()! : refreshToken);
        var error = json.RootElement.TryGetProperty("error", out var value) ? value.GetString() : null;
        if (error == "invalid_grant") return null;
        throw new InvalidOperationException($"Saved Microsoft sign-in failed: {error ?? response.StatusCode.ToString()}.");
    }

    private static async Task<MicrosoftTokens> DeviceCodeAsync(CancellationToken token)
    {
        using var device = await PostFormAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
            new Dictionary<string, string> { ["client_id"] = LunarClientId, ["scope"] = Scope }, token);
        var code = Required(device.RootElement, "device_code");
        var userCode = Required(device.RootElement, "user_code");
        var url = Required(device.RootElement, "verification_uri");
        var interval = device.RootElement.GetProperty("interval").GetInt32();
        var expires = device.RootElement.GetProperty("expires_in").GetInt32();
        Console.WriteLine("Microsoft will show this sign-in as Lunar Client (the app ID used by the original launcher).");
        Console.WriteLine($"Open {url} and enter code: {userCode}");
        Console.WriteLine("Waiting for you to complete sign-in on Microsoft's page...");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(expires);
        while (true)
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new InvalidOperationException("Sign-in code expired. Run launch again.");
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, interval)), token);
            using var response = await Http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = LunarClientId,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = code
                }), token);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            if (response.IsSuccessStatusCode)
            {
                return new MicrosoftTokens(Required(json.RootElement, "access_token"),
                    Required(json.RootElement, "refresh_token"));
            }
            var error = json.RootElement.TryGetProperty("error", out var value) ? value.GetString() : null;
            if (error == "authorization_pending") continue;
            if (error == "slow_down") { interval += 5; continue; }
            throw new InvalidOperationException($"Microsoft sign-in failed: {error ?? response.StatusCode.ToString()}.");
        }
    }

    private static async Task<JsonDocument> PostFormAsync(string url, Dictionary<string, string> fields, CancellationToken token)
    {
        using var response = await Http.PostAsync(url, new FormUrlEncodedContent(fields), token);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
    }

    private static async Task<JsonDocument> PostJsonAsync(string url, object body, CancellationToken token)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url, content, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Account verification failed at {new Uri(url).Host}: HTTP {(int)response.StatusCode}.");
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
    }

    private static string Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException($"Account response is missing {name}.");

    private static string GetJwtClaim(string jwt, string claim)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) throw new InvalidDataException("Invalid Minecraft token.");
        using var payload = JsonDocument.Parse(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/')
            .PadRight((parts[1].Length + 3) / 4 * 4, '=')));
        if (!payload.RootElement.TryGetProperty(claim, out var value))
            throw new InvalidDataException($"Minecraft token is missing {claim}.");
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString();
    }
}
