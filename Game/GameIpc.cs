using System.Net.WebSockets;

namespace BetterLC;

internal static class GameIpc
{
    private const string AuthService = "lunarclient.gameipc.auth.v1.AuthService";

    public static LoopbackServer Start(int port) => new(port, (_, socket, token) => RunAsync(socket, token));

    private static async Task RunAsync(WebSocket socket, CancellationToken token)
    {
        Console.WriteLine("Game IPC connected.");
        while (await LoopbackServer.ReceiveAsync(socket, token, 1024 * 1024) is { } message)
        {
            if (message.Type != WebSocketMessageType.Binary) continue;
            var requestId = ProtoWire.GetBytes(message.Data, 1);
            var service = ProtoWire.GetString(message.Data, 2);
            var method = ProtoWire.GetString(message.Data, 3);
            if (service != AuthService) continue;
            byte[] output = method == "OpenMicrosoftPopup" ? ProtoWire.VarintField(1, 2) : [];
            Console.WriteLine($"Game IPC auth request: {method}");
            var reply = ProtoWire.Message((1, ProtoWire.Message((1, requestId), (2, output))));
            await socket.SendAsync(reply, WebSocketMessageType.Binary, true, token);
        }
    }
}
