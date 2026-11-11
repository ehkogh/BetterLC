using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace BetterLC;

internal sealed class LoopbackServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly Func<string, WebSocket, CancellationToken, Task> handler;
    private readonly CancellationTokenSource stop = new();
    private readonly Task acceptLoop;

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public LoopbackServer(int port, Func<string, WebSocket, CancellationToken, Task> handler)
    {
        this.handler = handler;
        listener = new TcpListener(IPAddress.Loopback, port);
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
            try
            {
                var stream = client.GetStream();
                var request = await ReadUpgradeAsync(stream, token);
                if (request is not { } upgrade)
                {
                    await stream.WriteAsync("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), token);
                    return;
                }
                var accept = Convert.ToBase64String(SHA1.HashData(
                    Encoding.ASCII.GetBytes(upgrade.Key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n\r\n"), token);
                using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                {
                    IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30)
                });
                await handler(upgrade.Path, socket, token);
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);
            }
            catch (Exception ex) when (ex is IOException or WebSocketException or InvalidDataException
                                           or OperationCanceledException or CryptographicException) { }
        }
    }

    private static async Task<(string Path, string Key)?> ReadUpgradeAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new List<byte>();
        var one = new byte[1];
        while (header.Count < 4 || !(header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n'))
        {
            if (header.Count > 16 * 1024 || await stream.ReadAsync(one, token) == 0) return null;
            header.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine is not ["GET", var path, ..]) return null;
        string? key = null;
        var upgrade = false;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) return null;
            if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
                upgrade = value.Equals("websocket", StringComparison.OrdinalIgnoreCase);
            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
        }
        return upgrade && key is not null ? (path, key) : null;
    }

    public static async Task<(byte[] Data, WebSocketMessageType Type)?> ReceiveAsync(WebSocket socket,
        CancellationToken token, int maxBytes = 4 * 1024 * 1024)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (stream.Length + result.Count > maxBytes) throw new InvalidDataException("Oversized message.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (stream.ToArray(), result.MessageType);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        await acceptLoop;
        stop.Dispose();
    }
}
