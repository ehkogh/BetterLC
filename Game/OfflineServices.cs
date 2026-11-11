using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace BetterLC;

internal sealed class OfflineServices(string cacheRoot, string playerUuid)
{
    private const int LunarPlusPink = 0xFF69B4;
    private const int MaxActiveSprays = 5;
    private static readonly byte[] StatusOk = ProtoWire.VarintField(1, 1);
    private string WardrobePath => Path.Combine(cacheRoot, "offline", $"wardrobe-{Guid.Parse(playerUuid):N}.bin");
    private string LoadoutPath => Path.Combine(cacheRoot, "offline", $"loadout-{Guid.Parse(playerUuid):N}.bin");

    private async Task<(byte[] Emotes, byte[] Sprays)> LoadLoadoutAsync(CancellationToken token)
    {
        if (!File.Exists(LoadoutPath)) return ([], []);
        var data = await File.ReadAllBytesAsync(LoadoutPath, token);
        return (ProtoWire.GetBytes(data, 1), ProtoWire.GetBytes(data, 2));
    }

    private async Task<byte[]> SaveLoadoutAsync(byte[]? emotes, byte[]? sprays, CancellationToken token)
    {
        var (savedEmotes, savedSprays) = await LoadLoadoutAsync(token);
        Directory.CreateDirectory(Path.GetDirectoryName(LoadoutPath)!);
        await File.WriteAllBytesAsync(LoadoutPath,
            ProtoWire.Message((1, emotes ?? savedEmotes), (2, sprays ?? savedSprays)), token);
        return [];
    }

    public LoopbackServer Start() => new(0, async (path, socket, token) =>
    {
        if (path.StartsWith("/auth", StringComparison.Ordinal)) await AuthenticateAsync(socket, token);
        else if (path.StartsWith("/assets", StringComparison.Ordinal)) await ServeAssetsAsync(socket, token);
    });

    private async Task AuthenticateAsync(WebSocket socket, CancellationToken token)
    {
        if (await LoopbackServer.ReceiveAsync(socket, token) is null) return;
        using var rsa = RSA.Create(1024);
        var challenge = RandomNumberGenerator.GetBytes(4);
        await SendAsync(socket, ProtoWire.Message((1, ProtoWire.Message(
            (1, rsa.ExportSubjectPublicKeyInfo()), (2, challenge)))), token);

        if (await LoopbackServer.ReceiveAsync(socket, token) is not { } reply) return;
        var response = ProtoWire.GetBytes(reply.Data, 2);
        if (response.Length == 0) return;
        var secret = rsa.Decrypt(ProtoWire.GetBytes(response, 1), RSAEncryptionPadding.Pkcs1);
        var echoed = rsa.Decrypt(ProtoWire.GetBytes(response, 2), RSAEncryptionPadding.Pkcs1);
        if (!echoed.AsSpan().SequenceEqual(challenge)) return;

        var jwt = Encoding.UTF8.GetBytes(LocalJwt());
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[jwt.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(secret, tag.Length)) aes.Encrypt(nonce, jwt, cipher, tag);
        await SendAsync(socket, ProtoWire.Message((2, ProtoWire.Message((2, [.. nonce, .. cipher, .. tag])))), token);
        Console.WriteLine("Offline authenticator: signed in locally.");
        while (await LoopbackServer.ReceiveAsync(socket, token) is not null) { }
    }

    private string LocalJwt()
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Part("""{"alg":"HS256","typ":"JWT"}""") + "." +
               Part($$"""{"sub":"{{Guid.Parse(playerUuid)}}","iat":{{now}},"exp":{{now + 86400}}}""") + "." +
               Part(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
    }

    private async Task ServeAssetsAsync(WebSocket socket, CancellationToken token)
    {
        if (await LoopbackServer.ReceiveAsync(socket, token) is null) return;
        Console.WriteLine("Offline asset server connected.");
        while (await LoopbackServer.ReceiveAsync(socket, token) is { } message)
        {
            if (message.Type != WebSocketMessageType.Binary) continue;
            var id = ProtoWire.GetBytes(message.Data, 1);
            if (id.Length == 0) continue;
            var service = ProtoWire.GetString(message.Data, 2);
            var method = ProtoWire.GetString(message.Data, 3).ToLowerInvariant();
            var output = await AnswerAsync(ShortName(service), method, ProtoWire.GetBytes(message.Data, 4), token);
            await SendAsync(socket, ProtoWire.Message((1, ProtoWire.Message((1, id), (2, output)))), token);
        }
    }

    private async Task<byte[]> AnswerAsync(string service, string method, byte[] input, CancellationToken token)
    {
        switch (service, method)
        {
            case ("v2.CosmeticService", "login"):
            {
                var color = ProtoWire.VarintField(1, LunarPlusPink);
                var wardrobe = await Wardrobe.LoadAsync(WardrobePath, token);
                return [.. ProtoWire.VarintField(7, 1), .. ProtoWire.Message((3, color), (4, color)), .. wardrobe.LoginFields()];
            }
            case ("v2.CosmeticService", _):
            {
                var wardrobe = await Wardrobe.LoadAsync(WardrobePath, token);
                if (wardrobe.Apply(method, input) is not { } output) return [];
                await wardrobe.SaveAsync(WardrobePath, token);
                return output;
            }
            case ("v1.HostedWorldService", "login"): return ProtoWire.VarintField(5, 60);
            case ("v1.EmoteService", "login"):
            {
                var (emotes, _) = await LoadLoadoutAsync(token);
                return [.. ProtoWire.VarintField(5, 1), .. ProtoWire.Renumber(emotes, new Dictionary<int, int> { [1] = 2, [2] = 8 })];
            }
            case ("v1.EmoteService", "useemote"):
                return [.. StatusOk, .. ProtoWire.Renumber(input, new Dictionary<int, int> { [1] = 2, [2] = 3, [3] = 4 })];
            case ("v1.EmoteService", "updateequippedemotes"): return await SaveLoadoutAsync(input, null, token);
            case ("v1.SprayService", "login"):
            {
                var (_, sprays) = await LoadLoadoutAsync(token);
                return [.. ProtoWire.VarintField(4, 1), .. ProtoWire.VarintField(5, MaxActiveSprays),
                    .. ProtoWire.Renumber(sprays, new Dictionary<int, int> { [1] = 2 })];
            }
            case ("v1.SprayService", "usespray"): return StatusOk;
            case ("v1.SprayService", "updateequippedsprays"): return await SaveLoadoutAsync(null, input, token);
            case ("v1.BadgeService", "login"): return ProtoWire.VarintField(3, 1);
            default: return [];
        }
    }

    private static string ShortName(string service)
    {
        var last = service.LastIndexOf('.');
        return last <= 0 ? service : service[(service.LastIndexOf('.', last - 1) + 1)..];
    }

    private static Task SendAsync(WebSocket socket, byte[] data, CancellationToken token) =>
        socket.SendAsync(data, WebSocketMessageType.Binary, true, token);
}
