using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HighPop.Services;

/// <summary>Facepunch WebRCON client for Rust servers started with <c>rcon.web 1</c>.</summary>
public sealed class RconService : IDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = [];
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveLoop;
    private int _requestId;
    private volatile bool _authenticated;

    public bool IsConnected => _socket?.State == WebSocketState.Open && _authenticated;

    public async Task<bool> ConnectAsync(
        string host,
        int port,
        string password,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await DisconnectAsync(cancellationToken);
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            _socket = socket;

            var encodedPassword = Uri.EscapeDataString(password ?? string.Empty);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await socket.ConnectAsync(new Uri($"ws://{host}:{port}/{encodedPassword}"), timeout.Token);

            var connectionCts = new CancellationTokenSource();
            _connectionCts = connectionCts;
            _authenticated = socket.State == WebSocketState.Open;
            _receiveLoop = ReceiveLoopAsync(socket, connectionCts.Token);
            return _authenticated;
        }
        catch
        {
            await DisconnectAsync();
            return false;
        }
    }

    public async Task<string> SendCommandAsync(string command)
    {
        var socket = _socket;
        var connectionToken = _connectionCts?.Token ?? CancellationToken.None;
        if (!IsConnected || socket == null) return "[RCON] Not connected";

        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            return "[RCON] Could not allocate a command identifier";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var registration = timeout.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
                pending.TrySetCanceled(timeout.Token);
        });

        var request = JsonSerializer.Serialize(new
        {
            Identifier = id,
            Message = command,
            Name = "HighPop",
        });

        try
        {
            await _sendGate.WaitAsync(timeout.Token);
            try
            {
                await socket.SendAsync(
                    Encoding.UTF8.GetBytes(request),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    timeout.Token);
            }
            finally { _sendGate.Release(); }

            return await completion.Task;
        }
        catch (OperationCanceledException)
        {
            return IsConnected
                ? "[RCON] Timed out waiting for the Rust server response"
                : "[RCON] Connection closed while waiting for the Rust server response";
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            _authenticated = false;
            return "[RCON] Connection lost while sending the command";
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var payload = await ReceiveTextAsync(socket, token);
                if (string.IsNullOrWhiteSpace(payload)) continue;

                if (!TryReadResponse(payload, out var responseId, out var message))
                {
                    // A valid Rust response normally carries Identifier and Message. If a
                    // proxy strips the wrapper, only consume the raw payload when exactly
                    // one command is outstanding so it cannot be assigned to the wrong call.
                    if (_pending.Count == 1)
                    {
                        var pending = _pending.First();
                        if (_pending.TryRemove(pending.Key, out var rawCompletion))
                            rawCompletion.TrySetResult(payload);
                    }
                    continue;
                }

                // Identifier 0 is unsolicited console/chat output. HPRM obtains its visible
                // server console from the framework-specific authoritative log source.
                if (responseId == 0) continue;
                if (_pending.TryRemove(responseId, out var completion))
                    completion.TrySetResult(message);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        finally
        {
            _authenticated = false;
            foreach (var pending in _pending.Values)
                pending.TrySetCanceled();
            _pending.Clear();
        }
    }

    private static bool TryReadResponse(string payload, out int identifier, out string message)
    {
        identifier = -1;
        message = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!TryReadIdentifier(root, out identifier)) return false;
            if (!TryGetProperty(root, "Message", out var content))
            {
                message = payload;
                return true;
            }

            message = content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? string.Empty
                : content.GetRawText();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static bool TryReadIdentifier(JsonElement root, out int identifier)
    {
        identifier = -1;
        if (!TryGetProperty(root, "Identifier", out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt32(out identifier);
        return value.ValueKind == JsonValueKind.String
               && int.TryParse(value.GetString(), out identifier);
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
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
            if (result.MessageType == WebSocketMessageType.Text)
                stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _authenticated = false;
        var connectionCts = Interlocked.Exchange(ref _connectionCts, null);
        connectionCts?.Cancel();
        var receiveLoop = Interlocked.Exchange(ref _receiveLoop, null);
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket == null)
        {
            connectionCts?.Dispose();
            return;
        }

        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "HighPop disconnect",
                    timeout.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
        finally
        {
            socket.Dispose();
            if (receiveLoop != null)
            {
                try { await receiveLoop.WaitAsync(TimeSpan.FromMilliseconds(500)); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
            connectionCts?.Dispose();
            foreach (var pending in _pending.Values)
                pending.TrySetCanceled();
            _pending.Clear();
        }
    }

    public void Disconnect() => DisconnectAsync().GetAwaiter().GetResult();

    public void Dispose()
    {
        Disconnect();
        _sendGate.Dispose();
    }
}
