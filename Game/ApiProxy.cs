using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace BetterLC;

internal sealed class ApiProxy : IAsyncDisposable
{
    private static readonly Uri Upstream = new("https://api.lunarclientprod.com");
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> DroppedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-Installation-Id", "X-Overwolf-Muid", "sentry-trace", "baggage",
        "Host", "Connection", "Keep-Alive", "Content-Length", "Transfer-Encoding", "Accept-Encoding", "Upgrade", "Expect"
    };

    private static readonly HashSet<string> DisabledFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "DiscordCTA", "GamePromotionCTA", "PendingRewards", "YoutubeLinking", "BadGuysPromotion",
        "Trailer", "YouTooz", "Wrapped", "Advent"
    };

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly HttpClient http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly CancellationTokenSource stop = new();
    private readonly Task acceptLoop;

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public ApiProxy()
    {
        listener.Start();
        acceptLoop = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                _ = Task.Run(() => ServeAsync(client, stop.Token));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            var stream = client.GetStream();
            try
            {
                var request = await ReadRequestAsync(stream, token);
                if (request is null) return;
                using var response = await http.SendAsync(request, token);
                var body = await response.Content.ReadAsByteArrayAsync(token);
                if (response.IsSuccessStatusCode && request.RequestUri!.AbsolutePath == "/game/metadata")
                    body = CleanMetadata(body);
                await WriteResponseAsync(stream, response, body, token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await WriteStatusAsync(stream, 502, "Bad Gateway", token);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException
                                           or FormatException or ArgumentException) { }
        }
    }

    private static async Task<HttpRequestMessage?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 4 || !(head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (head.Count > MaxHeaderBytes || await stream.ReadAsync(one, token) == 0) return null;
            head.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines[0].Split(' ') is not [var method, var target, ..] || !target.StartsWith('/'))
            throw new InvalidDataException("Bad request line.");

        var headers = new List<(string Name, string Value)>();
        var length = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) length = int.Parse(value);
            if (!DroppedHeaders.Contains(name)) headers.Add((name, value));
        }
        if (length is < 0 or > MaxBodyBytes) throw new InvalidDataException("Body too large.");

        var request = new HttpRequestMessage(new HttpMethod(method), new Uri(Upstream, target));
        if (length > 0)
        {
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, token);
            request.Content = new ByteArrayContent(body);
        }
        foreach (var (name, value) in headers)
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpResponseMessage response, byte[] body,
        CancellationToken token)
    {
        var head = new StringBuilder($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\n");
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (name is "Transfer-Encoding" or "Content-Encoding" or "Content-Length" or "Connection") continue;
            foreach (var value in values) head.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        head.Append($"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), token);
        await stream.WriteAsync(body, token);
    }

    private static Task WriteStatusAsync(NetworkStream stream, int code, string reason, CancellationToken token) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token).AsTask();

    internal static byte[] CleanMetadata(byte[] body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            if ((root?["metaData"] ?? root) is not JsonObject meta || meta["featureFlag"] is null) return body;
            meta["blogPosts"] = new JsonArray();
            meta["pinnedServers"] = new JsonArray();
            meta.Remove("storeBadge");
            if (meta["featureFlag"] is JsonArray flags)
                foreach (var flag in flags.OfType<JsonObject>())
                {
                    var name = flag["identifier"]?.GetValue<string>() ?? "";
                    if (DisabledFlags.Contains(name) || name.Contains("Sentry", StringComparison.OrdinalIgnoreCase))
                        flag["value"] = false;
                }
            return Encoding.UTF8.GetBytes(root!.ToJsonString());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return body;
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        await acceptLoop;
        stop.Dispose();
        http.Dispose();
    }
}
