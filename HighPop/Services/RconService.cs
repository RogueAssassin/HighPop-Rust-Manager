using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HighPop.Services;

/// <summary>
/// Facepunch WebRCON client for Rust servers started with <c>rcon.web 1</c>.
/// <para>
/// The client is safe to use concurrently: sends are serialised internally (a WebSocket allows
/// one outstanding send) while responses are correlated by identifier, so callers must not wrap
/// <see cref="SendCommandAsync(string, CancellationToken)"/> in their own lock. Holding a lock
/// across the response wait is what made in-game chat lag: Rust never answers <c>say</c>, so
/// every message blocked the next command for the whole response timeout.
/// </para>
/// </summary>
public sealed class RconService : IDisposable, IAsyncDisposable
{
    /// <summary>Default wait for a correlated response to a command that is expected to answer.</summary>
    public static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Rust commands whose effect is broadcast as chat/unsolicited output and that never send a
    /// correlated (same-identifier) response. Waiting for one only stalls the caller.
    /// </summary>
    private static readonly HashSet<string> NoResponseCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "say", "global.say", "chat.say",
        "quit", "global.quit",
    };

    private const int MaxMessageBytes = 8 * 1024 * 1024;
    private const string ProbeCommand = "serverinfo";

    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = [];
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveLoop;
    private int _requestId;
    private volatile bool _authenticated;
    private int _disposed;

    public bool IsConnected => _socket?.State == WebSocketState.Open && _authenticated;

    /// <summary>
    /// True when <paramref name="command"/> is known never to receive a correlated response.
    /// Such commands are sent and complete as soon as the frame is written.
    /// </summary>
    public static bool ExpectsResponse(string? command)
    {
        var text = (command ?? string.Empty).TrimStart();
        if (text.Length == 0) return false;
        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return !NoResponseCommands.Contains(text[..end]);
    }

    public async Task<bool> ConnectAsync(
        string host,
        int port,
        string password,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await DisconnectAsync(cancellationToken).ConfigureAwait(false);
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            _socket = socket;

            var encodedPassword = Uri.EscapeDataString(password ?? string.Empty);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await socket.ConnectAsync(new Uri($"ws://{FormatHost(host)}:{port}/{encodedPassword}"), timeout.Token)
                    .ConfigureAwait(false);
            }

            var connectionCts = new CancellationTokenSource();
            _connectionCts = connectionCts;
            _authenticated = socket.State == WebSocketState.Open;
            _receiveLoop = ReceiveLoopAsync(socket, connectionCts.Token);
            if (!_authenticated) return false;

            // Rust accepts the WebSocket handshake before it validates the password, then closes
            // the socket. A cheap read-only probe tells the two cases apart: a rejected password
            // closes the connection (the probe is cancelled), while a busy-but-valid server may
            // simply answer slowly, which still counts as connected.
            await SendCommandAsync(ProbeCommand, TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _authenticated = socket.State == WebSocketState.Open;
            if (!_authenticated) await DisconnectAsync().ConfigureAwait(false);
            return _authenticated;
        }
        catch
        {
            await DisconnectAsync().ConfigureAwait(false);
            return false;
        }
    }

    public Task<string> SendCommandAsync(
        string command,
        CancellationToken cancellationToken = default)
        => SendCommandAsync(command, DefaultResponseTimeout, cancellationToken);

    /// <summary>
    /// Sends <paramref name="command"/> and returns Rust's correlated response. Commands that
    /// never answer (see <see cref="ExpectsResponse"/>) return an empty string as soon as they are
    /// written. Failures are reported as <c>[RCON] ...</c> strings rather than exceptions.
    /// </summary>
    public async Task<string> SendCommandAsync(
        string command,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken = default)
    {
        var socket = _socket;
        var connectionToken = _connectionCts?.Token ?? CancellationToken.None;
        if (!IsConnected || socket == null) return "[RCON] Not connected";
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;

        var expectsResponse = ExpectsResponse(command);
        var id = NextIdentifier();
        TaskCompletionSource<string>? completion = null;
        if (expectsResponse)
        {
            completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion))
                return "[RCON] Could not allocate a command identifier";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, cancellationToken);
        timeout.CancelAfter(responseTimeout > TimeSpan.Zero ? responseTimeout : DefaultResponseTimeout);
        using var registration = completion == null
            ? default
            : timeout.Token.Register(static state =>
            {
                var (pending, key, token) = ((ConcurrentDictionary<int, TaskCompletionSource<string>>, int, CancellationToken))state!;
                if (pending.TryRemove(key, out var entry)) entry.TrySetCanceled(token);
            }, (_pending, id, timeout.Token));

        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Identifier = id,
            Message = command,
            Name = "HighPop",
        });

        try
        {
            await _sendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(request, WebSocketMessageType.Text, endOfMessage: true, timeout.Token)
                    .ConfigureAwait(false);
            }
            finally { _sendGate.Release(); }

            return completion == null
                ? string.Empty
                : await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) return "[RCON] Command cancelled";
            return IsConnected
                ? "[RCON] No response from the Rust server within " +
                  $"{(responseTimeout > TimeSpan.Zero ? responseTimeout : DefaultResponseTimeout).TotalSeconds:0}s"
                : "[RCON] Connection closed while waiting for the Rust server response";
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            _authenticated = false;
            return "[RCON] Connection lost while sending the command";
        }
        finally
        {
            if (completion != null) _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Positive identifiers only: Rust reserves 0 and -1 for unsolicited console, chat and
    /// generic output, so a wrapped counter must never collide with them.
    /// </summary>
    private int NextIdentifier()
    {
        while (true)
        {
            var id = Interlocked.Increment(ref _requestId) & int.MaxValue;
            if (id > 0) return id;
        }
    }

    private static string FormatHost(string host)
    {
        host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        return host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var payload = await ReceiveTextAsync(socket, buffer, token).ConfigureAwait(false);
                if (payload == null) break; // close frame
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

                // Identifiers 0 and -1 are unsolicited console/chat output. HPRM obtains its visible
                // server console from the framework-specific authoritative log source.
                if (responseId <= 0) continue;
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
            CancelPending();
        }
    }

    internal static bool TryReadResponse(string payload, out int identifier, out string message)
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

    /// <summary>Returns the next text message, or <c>null</c> when the server closed the socket.</summary>
    private static async Task<string?> ReceiveTextAsync(
        ClientWebSocket socket,
        byte[] buffer,
        CancellationToken token)
    {
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        // Fast path: Rust sends almost every response as a single frame.
        if (result.EndOfMessage)
            return result.MessageType == WebSocketMessageType.Text
                ? Encoding.UTF8.GetString(buffer, 0, result.Count)
                : string.Empty;

        using var stream = new MemoryStream();
        var isText = result.MessageType == WebSocketMessageType.Text;
        if (isText) stream.Write(buffer, 0, result.Count);
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (isText && stream.Length + result.Count <= MaxMessageBytes)
                stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return isText ? Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length) : string.Empty;
    }

    private void CancelPending()
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var pending))
                pending.TrySetCanceled();
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _authenticated = false;
        var connectionCts = Interlocked.Exchange(ref _connectionCts, null);
        try { connectionCts?.Cancel(); } catch (ObjectDisposedException) { }
        var receiveLoop = Interlocked.Exchange(ref _receiveLoop, null);
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket == null)
        {
            connectionCts?.Dispose();
            CancelPending();
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
                    timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            socket.Dispose();
            if (receiveLoop != null)
            {
                try { await receiveLoop.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { }
            }
            connectionCts?.Dispose();
            CancelPending();
        }
    }

    /// <summary>
    /// Synchronous teardown that never waits. The previous implementation blocked with
    /// <c>DisconnectAsync().GetAwaiter().GetResult()</c>; when that ran on the WPF UI thread
    /// (e.g. the <c>using var rcon</c> in the Oxide stop path) the awaited close continuation
    /// was queued back to the blocked UI thread and the whole app deadlocked.
    /// Use <see cref="DisconnectAsync"/> / <see cref="DisposeAsync"/> for a graceful close.
    /// </summary>
    public void Disconnect()
    {
        _authenticated = false;
        var connectionCts = Interlocked.Exchange(ref _connectionCts, null);
        Interlocked.Exchange(ref _receiveLoop, null);
        var socket = Interlocked.Exchange(ref _socket, null);
        try { connectionCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { socket?.Abort(); } catch { }
        try { socket?.Dispose(); } catch { }
        try { connectionCts?.Dispose(); } catch { }
        CancelPending();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Disconnect();
        // The send gate is intentionally not disposed: a concurrent sender may still be
        // releasing it, and SemaphoreSlim holds no unmanaged resources without a wait handle.
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await DisconnectAsync().ConfigureAwait(false);
    }
}
