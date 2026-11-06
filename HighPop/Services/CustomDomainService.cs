using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HighPop.Services;

public enum DomainCheckSeverity { Ok, Info, Warning, Error }

public sealed record DomainCheckItem(DomainCheckSeverity Severity, string Title, string Detail)
{
    public string Icon => Severity switch
    {
        DomainCheckSeverity.Ok => "✅",
        DomainCheckSeverity.Info => "ℹ",
        DomainCheckSeverity.Warning => "⚠",
        _ => "❌",
    };
}

public sealed record DomainCheckReport(
    string Hostname,
    IReadOnlyList<DomainCheckItem> Items,
    string ConnectCommand,
    string? PublicIp)
{
    public bool HasErrors => Items.Any(item => item.Severity == DomainCheckSeverity.Error);
    public string Summary => HasErrors
        ? "Players cannot join through this domain yet — fix the ❌ items."
        : Items.Any(item => item.Severity == DomainCheckSeverity.Warning)
            ? "The domain resolves, but review the ⚠ items."
            : "DNS is set up the way Rust expects.";
}

/// <summary>
/// Checks a custom join domain against the records Facepunch documents for Rust
/// (https://wiki.facepunch.com/rust/dns-records): an A record to the server's public IPv4
/// address and a single <c>_rust._udp</c> SRV record pointing at it with the game port.
/// Lookups use Cloudflare DNS-over-HTTPS so the authoritative answer is seen, not a stale
/// local cache, and so SRV records can be read without a native resolver.
/// </summary>
public sealed partial class CustomDomainService
{
    private const string DohEndpoint = "https://cloudflare-dns.com/dns-query";
    private const string TraceEndpoint = "https://1.1.1.1/cdn-cgi/trace";
    private static readonly HttpClient Http = CreateClient();

    // Cloudflare's published proxy (orange-cloud) IPv4 ranges: https://www.cloudflare.com/ips-v4
    private static readonly (uint Network, uint Mask)[] CloudflareRanges = new[]
    {
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22",
        "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20",
        "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
    }.Select(ParseCidr).ToArray();

    /// <summary>Normalises operator input such as <c>https://Rust.Example.com:28015/</c> to <c>rust.example.com</c>.</summary>
    public static string NormalizeHostname(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) text = text[(scheme + 3)..];
        var slash = text.IndexOf('/');
        if (slash >= 0) text = text[..slash];
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out _)) text = text[..colon];
        return text.TrimEnd('.').ToLowerInvariant();
    }

    public static bool IsValidHostname(string? hostname) =>
        !string.IsNullOrEmpty(hostname)
        && hostname.Length <= 253
        && !IPAddress.TryParse(hostname, out _)
        && HostnameRegex().IsMatch(hostname);

    /// <summary>The F1 console command players use: no port is needed once the SRV record exists.</summary>
    public static string BuildConnectCommand(string hostname, int gamePort, bool hasSrv) =>
        hasSrv || gamePort == 28015
            ? $"client.connect {hostname}" + (hasSrv ? string.Empty : ":28015")
            : $"client.connect {hostname}:{gamePort}";

    public static bool IsCloudflareProxyAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var value = ToUInt32(address);
        return CloudflareRanges.Any(range => (value & range.Mask) == range.Network);
    }

    public static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 100 && b[1] is >= 64 and <= 127) // carrier-grade NAT
               || (b[0] == 169 && b[1] == 254);
    }

    public async Task<DomainCheckReport> CheckAsync(
        string hostnameInput,
        int gamePort,
        CancellationToken cancellationToken = default)
    {
        var hostname = NormalizeHostname(hostnameInput);
        var items = new List<DomainCheckItem>();
        if (!IsValidHostname(hostname))
        {
            items.Add(new(DomainCheckSeverity.Error, "Not a valid hostname",
                "Enter just the name, for example rust.example.com — no http://, path, or port."));
            return new DomainCheckReport(hostname, items, string.Empty, null);
        }

        var publicIpTask = TryGetPublicIpAsync(cancellationToken);
        var aTask = QueryAsync(hostname, "A", cancellationToken);
        var aaaaTask = QueryAsync(hostname, "AAAA", cancellationToken);
        var srvName = "_rust._udp." + hostname;
        var srvTask = QueryAsync(srvName, "SRV", cancellationToken);
        await Task.WhenAll(publicIpTask, aTask, aaaaTask, srvTask).ConfigureAwait(false);
        var publicIp = publicIpTask.Result;
        var a = aTask.Result;
        var aaaa = aaaaTask.Result;
        var srv = srvTask.Result;

        if (a == null)
        {
            items.Add(new(DomainCheckSeverity.Error, "DNS lookup failed",
                "HighPop could not reach Cloudflare DNS-over-HTTPS (cloudflare-dns.com). Check this PC's internet access and try again."));
            return new DomainCheckReport(hostname, items, BuildConnectCommand(hostname, gamePort, false), publicIp?.ToString());
        }

        // ── A record ────────────────────────────────────────────────────────────
        var addresses = a.Answers.Where(answer => answer.Type == 1)
            .Select(answer => IPAddress.TryParse(answer.Data, out var ip) ? ip : null)
            .OfType<IPAddress>()
            .ToList();
        var cname = a.Answers.FirstOrDefault(answer => answer.Type == 5)?.Data.TrimEnd('.');
        if (a.Status == 3)
            items.Add(new(DomainCheckSeverity.Error, $"{hostname} does not exist in DNS",
                "Public DNS (including the domain's own nameservers) returns NXDOMAIN. Create an A record for this exact " +
                "name at your DNS provider. On Cloudflare: DNS → Records → Add record → Type A, Name " +
                $"'{hostname.Split('.')[0]}', IPv4 your public IP{(publicIp == null ? string.Empty : $" ({publicIp})")}, Proxy status DNS only (grey cloud)."));
        else if (addresses.Count == 0)
            items.Add(new(DomainCheckSeverity.Error, "No A (IPv4) record",
                "The name exists but has no IPv4 address. Rust joins over IPv4: add an A record pointing at the server's public IP."));
        else
        {
            if (cname != null)
                items.Add(new(DomainCheckSeverity.Info, $"Resolves through CNAME {cname}",
                    "This works, but the SRV record must target a name that has an A record. Pointing the SRV at this hostname is fine."));
            if (addresses.Count > 1)
                items.Add(new(DomainCheckSeverity.Warning, $"{addresses.Count} A records",
                    $"{string.Join(", ", addresses)} — players are sent to any of them. Keep a single A record for one Rust server."));

            foreach (var address in addresses)
            {
                if (IsCloudflareProxyAddress(address))
                    items.Add(new(DomainCheckSeverity.Error, $"{address} is a Cloudflare proxy address (orange cloud)",
                        "Cloudflare's proxy only carries HTTP/HTTPS. Rust game traffic is UDP and is dropped. In Cloudflare → DNS → " +
                        "Records, edit this record and switch Proxy status to 'DNS only' (grey cloud)."));
                else if (IsPrivateAddress(address))
                    items.Add(new(DomainCheckSeverity.Error, $"{address} is a private/LAN address",
                        "Players on the internet cannot reach a LAN or CGNAT address. Use your public IP and port-forward UDP on your router."));
                else if (publicIp != null && !address.Equals(publicIp))
                    items.Add(new(DomainCheckSeverity.Warning, $"Points to {address}, this PC's public IP is {publicIp}",
                        "If Rust runs on this machine/network, update the A record (a changed dynamic IP is the usual cause). " +
                        "If the server is hosted elsewhere, this is expected."));
                else
                    items.Add(new(DomainCheckSeverity.Ok, $"A record → {address}",
                        publicIp != null ? "Matches this network's public IP and is DNS only." : "Public, DNS-only address."));
            }
        }

        if (aaaa?.Answers.Any(answer => answer.Type == 28) == true)
            items.Add(new(DomainCheckSeverity.Info, "Has an AAAA (IPv6) record",
                "Rust connects over IPv4. An AAAA record is harmless if the A record is correct, but remove it if IPv6 is not forwarded to the server."));

        // ── SRV record ──────────────────────────────────────────────────────────
        var srvRecords = srv?.Answers.Where(answer => answer.Type == 33)
            .Select(answer => ParseSrv(answer.Data))
            .OfType<(int Priority, int Weight, int Port, string Target)>()
            .ToList() ?? [];
        var hasValidSrv = false;
        if (srvRecords.Count == 0)
            items.Add(new(DomainCheckSeverity.Warning, $"No SRV record {srvName}",
                $"Without it players must type the port ({hostname}:{gamePort}) and favourites cannot follow IP/port changes. " +
                $"Add SRV: service _rust, protocol _udp, name {hostname.Split('.')[0]}, priority 0, weight 0, port {gamePort}, target {hostname}."));
        else
        {
            if (srvRecords.Count > 1)
                items.Add(new(DomainCheckSeverity.Error, $"{srvRecords.Count} SRV records",
                    "Facepunch requires exactly one _rust._udp SRV entry per server. Delete the extras."));
            foreach (var record in srvRecords)
            {
                var targetOk = record.Target.Equals(hostname, StringComparison.OrdinalIgnoreCase)
                               || (cname != null && record.Target.Equals(cname, StringComparison.OrdinalIgnoreCase));
                if (record.Port != gamePort)
                    items.Add(new(DomainCheckSeverity.Error, $"SRV port {record.Port} ≠ game port {gamePort}",
                        "The SRV record must use the game port (server.port), not the query, RCON or Rust+ port."));
                else if (!targetOk)
                    items.Add(new(DomainCheckSeverity.Warning, $"SRV target is {record.Target}",
                        $"Facepunch's setup points the SRV at the A record ({hostname}). Make sure {record.Target} has an A record to the server."));
                else
                {
                    hasValidSrv = srvRecords.Count == 1;
                    items.Add(new(DomainCheckSeverity.Ok, $"SRV {srvName} → {record.Target}:{record.Port}",
                        "Players can connect with just the hostname."));
                }
            }
        }

        items.Add(new(DomainCheckSeverity.Info, "Ports must be forwarded",
            $"Forward UDP {gamePort} (game) to the server. Joining your own domain from inside your LAN needs NAT loopback " +
            "(hairpin NAT) on the router — test from outside, e.g. a phone hotspot, before assuming DNS is wrong."));

        return new DomainCheckReport(hostname, items, BuildConnectCommand(hostname, gamePort, hasValidSrv), publicIp?.ToString());
    }

    // ── DNS-over-HTTPS (application/dns-json) ─────────────────────────────────

    private sealed record DohAnswer(int Type, string Data);
    private sealed record DohResult(int Status, List<DohAnswer> Answers);

    private static async Task<DohResult?> QueryAsync(string name, string type, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{DohEndpoint}?name={Uri.EscapeDataString(name)}&type={type}");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseDohJson(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    private static DohResult ParseDohJson(JsonElement root)
    {
        var status = root.TryGetProperty("Status", out var s) && s.TryGetInt32(out var code) ? code : 2;
        var answers = new List<DohAnswer>();
        if (root.TryGetProperty("Answer", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var entry in list.EnumerateArray())
                if (entry.TryGetProperty("type", out var t) && t.TryGetInt32(out var answerType)
                    && entry.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String)
                    answers.Add(new DohAnswer(answerType, d.GetString() ?? string.Empty));
        return new DohResult(status, answers);
    }

    /// <summary>Parses SRV RDATA text <c>priority weight port target.</c>.</summary>
    public static (int Priority, int Weight, int Port, string Target)? ParseSrv(string data)
    {
        var parts = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 4
               && int.TryParse(parts[0], out var priority)
               && int.TryParse(parts[1], out var weight)
               && int.TryParse(parts[2], out var port)
            ? (priority, weight, port, parts[3].TrimEnd('.').ToLowerInvariant())
            : null;
    }

    private static async Task<IPAddress?> TryGetPublicIpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var trace = await Http.GetStringAsync(TraceEndpoint, cancellationToken).ConfigureAwait(false);
            foreach (var line in trace.Split('\n'))
                if (line.StartsWith("ip=", StringComparison.Ordinal)
                    && IPAddress.TryParse(line[3..].Trim(), out var ip)
                    && ip.AddressFamily == AddressFamily.InterNetwork)
                    return ip;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        return null;
    }

    private static HttpClient CreateClient()
    {
        // Prefer IPv4 so the trace endpoint reports the IPv4 address players will use.
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, token)
                        .ConfigureAwait(false);
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(RogueGamingEndpoints.UserAgent);
        return client;
    }

    private static (uint Network, uint Mask) ParseCidr(string cidr)
    {
        var slash = cidr.IndexOf('/');
        var bits = int.Parse(cidr[(slash + 1)..]);
        var mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
        return (ToUInt32(IPAddress.Parse(cidr[..slash])) & mask, mask);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    [GeneratedRegex(@"^(?=.{1,253}$)(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))+$")]
    private static partial Regex HostnameRegex();
}
