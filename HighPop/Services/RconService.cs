using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HighPop.Services;

/// <summary>Facepunch WebRCON client for Rust servers started with <c>rcon.web 1</c>.</summary>
public sealed class RconService : IDisposable
{
    private ClientWebSocket? _socket;
    private int _requestId;
    private bool _authenticated;

    public bool IsConnected => _socket?.State == WebSocketState.Open && _authenticated;

    public async Task<bool> ConnectAsync(string host, int port, string password)
    {
        try
        {
            Disconnect();
            _socket = new ClientWebSocket();
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            var encodedPassword = Uri.EscapeDataString(password ?? string.Empty);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _socket.ConnectAsync(new Uri($"ws://{host}:{port}/{encodedPassword}"), timeout.Token);
            _authenticated = _socket.State == WebSocketState.Open;
            return _authenticated;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    public async Task<string> SendCommandAsync(string command)
    {
        if (!IsConnected || _socket == null) return "[RCON] Not connected";

        var id = Interlocked.Increment(ref _requestId);
        var request = JsonSerializer.Serialize(new
        {
            Identifier = id,
            Message = command,
            Name = "HighPop",
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _socket.SendAsync(
            Encoding.UTF8.GetBytes(request),
            WebSocketMessageType.Text,
            endOfMessage: true,
            timeout.Token);

        try
        {
            while (!timeout.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var payload = await ReceiveTextAsync(_socket, timeout.Token);
                if (string.IsNullOrWhiteSpace(payload)) continue;
                try
                {
                    using var document = JsonDocument.Parse(payload);
                    var root = document.RootElement;
                    var responseId = root.TryGetProperty("Identifier", out var identifier)
                        ? identifier.GetInt32()
                        : -1;
                    if (responseId != id) continue;
                    return root.TryGetProperty("Message", out var message)
                        ? message.GetString() ?? string.Empty
                        : payload;
                }
                catch (JsonException)
                {
                    return payload;
                }
            }
        }
        catch (OperationCanceledException) { }

        return "[RCON] Timed out waiting for the Rust server response";
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close) return string.Empty;
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void Disconnect()
    {
        _authenticated = false;
        if (_socket?.State == WebSocketState.Open)
        {
            try
            {
                _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "HighPop disconnect",
                    CancellationToken.None).Wait(500);
            }
            catch { }
        }
        _socket?.Dispose();
        _socket = null;
    }

    public void Dispose() => Disconnect();
}
