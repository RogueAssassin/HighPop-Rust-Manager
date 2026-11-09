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
        LastError = string.Empty;
        if (!CanSendPassword(password, out var passwordProblem))
        {
            LastError = passwordProblem;
            return false;
        }

        try
        {
            await DisconnectAsync(cancellationToken).ConfigureAwait(false);
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            _socket = socket;

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                await socket.ConnectAsync(BuildEndpoint(host, port, password), timeout.Token)
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
            if (!_authenticated)
            {
                LastError = "Rust closed the connection. The RCON password does not match the server's rcon.password.";
                await DisconnectAsync().ConfigureAwait(false);
            }
            return _authenticated;
        }
        catch (Exception ex)
        {
            if (LastError.Length == 0)
                LastError = ex is OperationCanceledException
                    ? "Rust WebRCON did not answer in time."
                    : "Rust WebRCON is not reachable: " + ex.Message;
            await DisconnectAsync().ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Why the last <see cref="ConnectAsync"/> failed, in words an operator can act on.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// Rust compares the raw WebSocket request path with <c>rcon.password</c> and never URL-decodes
    /// it (Facepunch.Rcon uses <c>ConnectionInfo.Path.Substring(1)</c>). Percent-encoding a password
    /// therefore makes every password with a symbol fail, so HighPop sends it unencoded. A password
    /// containing a character a URL cannot carry unencoded cannot be used over WebRCON at all.
    /// </summary>
    public static bool CanSendPassword(string? password, out string problem)
    {
        problem = string.Empty;
        if (string.IsNullOrEmpty(password))
        {
            problem = "No RCON password is set.";
            return false;
        }

        var bad = password.FirstOrDefault(c =>
            c <= ' ' || c >= 0x7F || UnsendablePasswordCharacters.Contains(c));
        if (bad != default)
        {
            problem = "The RCON password contains " + DescribeCharacter(bad) +
                      ", which Rust WebRCON cannot receive. Use letters, numbers and - _ . ~ ! $ & ' ( ) * + , ; = : @ only.";
            return false;
        }

        if (BuildEndpoint("127.0.0.1", 28016, password).AbsolutePath != "/" + password)
        {
            problem = "The RCON password changes when it is put in a URL, so Rust would reject it. Use letters and numbers.";
            return false;
        }
        return true;
    }

    private const string UnsendablePasswordCharacters = "\"#%/<>?[\\]^`{|}";

    private static string DescribeCharacter(char c) => c switch
    {
        ' ' => "a space",
        '"' => "a double quote",
        _ when char.IsControl(c) => "a control character",
        _ when c >= 0x7F => $"the non-ASCII character '{c}'",
        _ => $"'{c}'",
    };

    internal static Uri BuildEndpoint(string host, int port, string password)
        => new($"ws://{FormatHost(host)}:{port}/{password}");

    /// <summary>In-game chat (including <c>say</c> from any source), relayed by Rust to every WebRCON client.</summary>
    public event Action<RconChatMessage>? ChatReceived;

    /// <summary>F7 player reports, relayed by Rust as WebRCON <c>Report</c> broadcasts.</summary>
    public event Action<string>? ReportReceived;

    private readonly ConcurrentDictionary<long, (string Expected, TaskCompletionSource<bool> Seen)> _chatWaiters = [];
    private long _chatWaiterId;

    /// <summary>
    /// Sends a <c>say</c>-style command and reports whether Rust actually broadcast it: Rust gives
    /// <c>say</c> no correlated reply, but every WebRCON client receives the chat line it produced.
    /// </summary>
    public async Task<RconSayResult> SayAsync(
        string command,
        TimeSpan confirmTimeout,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return new RconSayResult(false, false, "[RCON] Not connected");
        var expected = SayText(command);
        var waiterId = Interlocked.Increment(ref _chatWaiterId);
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _chatWaiters[waiterId] = (NormalizeChat(expected), seen);
        try
        {
            var reply = await SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
            if (reply.StartsWith("[RCON]", StringComparison.Ordinal))
                return new RconSayResult(false, false, reply);

            var finished = await Task.WhenAny(seen.Task, Task.Delay(confirmTimeout, cancellationToken))
                .ConfigureAwait(false);
            return new RconSayResult(true, finished == seen.Task, reply);
        }
        catch (OperationCanceledException)
        {
            return new RconSayResult(true, false, string.Empty);
        }
        finally
        {
            _chatWaiters.TryRemove(waiterId, out _);
        }
    }

    /// <summary>The chat text a <c>say</c>/<c>global.say</c>/<c>chat.say</c> command broadcasts.</summary>
    public static string SayText(string command)
    {
        var text = (command ?? string.Empty).Trim();
        var space = text.IndexOfAny([' ', '\t']);
        return space < 0 ? string.Empty : text[(space + 1)..].Trim();
    }

    internal static string NormalizeChat(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text)
        {
            if (c == '"') continue;
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && builder.Length > 0) builder.Append(' ');
                lastWasSpace = true;
                continue;
            }
            builder.Append(char.ToLowerInvariant(c));
            lastWasSpace = false;
        }
        return builder.ToString().Trim();
    }

    private void DispatchBroadcast(string type, string message)
    {
        if (type.Equals("Report", StringComparison.OrdinalIgnoreCase))
        {
            ReportReceived?.Invoke(message);
            return;
        }
        if (!type.Equals("Chat", StringComparison.OrdinalIgnoreCase)
            || !RconChatMessage.TryParse(message, out var chat))
            return;

        var normalized = NormalizeChat(chat.Message);
        foreach (var waiter in _chatWaiters.Values)
        {
            if (waiter.Expected.Length > 0
                && (normalized == waiter.Expected || normalized.Contains(waiter.Expected, StringComparison.Ordinal)))
                waiter.Seen.TrySetResult(true);
        }
        ChatReceived?.Invoke(chat);
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

                if (!TryReadResponse(payload, out var responseId, out var message, out var messageType))
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
                // server console from the framework-specific authoritative log source, so only
                // chat and F7 reports are taken from here.
                if (responseId <= 0)
                {
                    try { DispatchBroadcast(messageType, message); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                    continue;
                }
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
        => TryReadResponse(payload, out identifier, out message, out _);

    internal static bool TryReadResponse(string payload, out int identifier, out string message, out string type)
    {
        identifier = -1;
        message = string.Empty;
        type = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!TryReadIdentifier(root, out identifier)) return false;
            if (TryGetProperty(root, "Type", out var typeValue))
                type = typeValue.ValueKind == JsonValueKind.Number && typeValue.TryGetInt32(out var typeNumber)
                    ? typeNumber switch { 3 => "Chat", 4 => "Report", _ => typeNumber.ToString() }
                    : typeValue.ToString();
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

/// <summary>Outcome of <see cref="RconService.SayAsync"/>.</summary>
/// <param name="Sent">Rust accepted the command frame.</param>
/// <param name="Delivered">Rust broadcast the chat line back, so players saw it.</param>
/// <param name="Reply">Any reply or <c>[RCON]</c> error text.</param>
public sealed record RconSayResult(bool Sent, bool Delivered, string Reply);

/// <summary>One in-game chat line relayed by Rust WebRCON (a <c>Chat</c> broadcast).</summary>
public sealed record RconChatMessage(
    int Channel,
    string Message,
    string UserId,
    string Username,
    DateTime Time)
{
    public bool IsServer => UserId is "" or "0" || Username.Equals("SERVER", StringComparison.OrdinalIgnoreCase);

    public string ChannelText => Channel switch
    {
        0 => "Global",
        1 => "Team",
        2 => "Server",
        3 => "Cards",
        4 => "Local",
        5 => "Clan",
        _ => $"Channel {Channel}",
    };

    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss");

    public static bool TryParse(string json, out RconChatMessage chat)
    {
        chat = new RconChatMessage(0, string.Empty, string.Empty, string.Empty, DateTime.UtcNow);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryRead(document.RootElement, out chat)) return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Parses <c>chat.tail</c> output: a JSON array of chat entries, oldest first.</summary>
    public static List<RconChatMessage> ParseTail(string json)
    {
        var result = new List<RconChatMessage>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in document.RootElement.EnumerateArray())
                if (TryRead(item, out var chat)) result.Add(chat);
        }
        catch (JsonException) { }
        return result;
    }

    private static bool TryRead(JsonElement root, out RconChatMessage chat)
    {
        chat = new RconChatMessage(0, string.Empty, string.Empty, string.Empty, DateTime.UtcNow);
        if (root.ValueKind != JsonValueKind.Object) return false;
        string message = string.Empty, userId = string.Empty, username = string.Empty;
        int channel = 0;
        long time = 0;
        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name.ToLowerInvariant())
            {
                case "message": message = value.ToString(); break;
                case "userid": userId = value.ToString(); break;
                case "username": username = value.ToString(); break;
                case "channel": if (value.ValueKind == JsonValueKind.Number) value.TryGetInt32(out channel); break;
                case "time": if (value.ValueKind == JsonValueKind.Number) value.TryGetInt64(out time); break;
            }
        }
        if (message.Length == 0) return false;
        var when = time > 0 && time < 32503680000
            ? DateTimeOffset.FromUnixTimeSeconds(time).UtcDateTime
            : DateTime.UtcNow;
        chat = new RconChatMessage(
            channel,
            UiTextSanitizer.Normalize(message),
            UiTextSanitizer.Normalize(userId, UiTextSanitizer.MaxLabelLength),
            UiTextSanitizer.Normalize(username, UiTextSanitizer.MaxLabelLength),
            when);
        return true;
    }
}
