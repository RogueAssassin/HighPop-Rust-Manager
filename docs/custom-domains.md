# Joining a Rust server with a custom domain

Players can join with a name such as `rust.example.com` instead of an IP address. Rust supports this
natively ([Facepunch: Server DNS Records](https://wiki.facepunch.com/rust/dns-records)) when the domain
has **two** records:

| Record | Name | Points to | Notes |
|---|---|---|---|
| **A** | `rust.example.com` | your server's **public IPv4** | Must be **DNS only** — never proxied |
| **SRV** | `_rust._udp.rust.example.com` | `rust.example.com`, port = **game port** (`server.port`, default `28015`) | Exactly **one** SRV entry per server |

With both records, players type in the F1 console:

```text
client.connect rust.example.com
```

Without the SRV record the name still works, but players must add the port
(`client.connect rust.example.com:28015`) and favourites cannot follow IP/port changes.

> HighPop checks all of this for you: **Server → Configure → Rust → Custom join domain → Check DNS**.

## 1. Before you start

1. **Find your public IP.** On the server's network, open <https://1.1.1.1/cdn-cgi/trace> and read the
   `ip=` line (HighPop's DNS check also shows it). It must be a public address — not `192.168.x.x`,
   `10.x.x.x`, `172.16–31.x.x` or `100.64–127.x.x` (carrier-grade NAT; ask your ISP for a public IP).
2. **Is the IP static?** If your ISP changes it, set up dynamic DNS (see [Dynamic IP](#dynamic-ip)).
3. **Note the game port** from HighPop (Server → Configure → Settings → Game port). The SRV record uses this port,
   not the query, RCON or Rust+ port.

## 2. Cloudflare

Cloudflare's orange-cloud proxy only carries HTTP/HTTPS. Rust is UDP, so a **proxied record cannot be
used to join** — the client resolves to a Cloudflare edge address that drops game traffic. Game records
must be **DNS only** (grey cloud). Carrying UDP through Cloudflare needs Cloudflare Spectrum, a paid add-on.

1. Cloudflare dashboard → select your domain → **DNS → Records**.
2. **Add record**
   - Type: `A`
   - Name: `rust` (the part before your domain)
   - IPv4 address: your public IP
   - Proxy status: **DNS only** (click the orange cloud until it is grey)
   - TTL: `Auto`
3. **Add record**
   - Type: `SRV`
   - Name: `_rust._udp.rust` — Cloudflare puts service, protocol and name together in the Name box
   - Priority: `0`, Weight: `0`
   - Port: your game port, e.g. `28015`
   - Target: `rust.example.com` (the A record from step 2 — the full name)
   - TTL: `Auto`
4. Save, wait a minute, then run **Check DNS** in HighPop.

Common Cloudflare mistakes:

- The record is **proxied** (orange cloud). HighPop reports "Cloudflare proxy address".
- The record was added to a **different zone or account**, or with a typo in Name, so the name returns
  `NXDOMAIN` ("does not exist in DNS").
- The SRV **Target** is an IP address, or points to a proxied record.
- A **CNAME** to the root domain is used: if the root is proxied, the game name is proxied too. Use an
  `A` record for the game name.
- An `AAAA` record exists but IPv6 is not forwarded to the server.

## 3. Other DNS providers

The values are always the same; only the field names differ.

| Provider | A record | SRV record |
|---|---|---|
| **GoDaddy** | Type `A`, Name `rust`, Value = public IP | Type `SRV`, Service `_rust`, Protocol `_udp`, Name `rust`, Priority `0`, Weight `0`, Port = game port, Target `rust.example.com` |
| **Namecheap** (Advanced DNS) | `A Record`, Host `rust`, Value = public IP | `SRV Record`, Service `_rust`, Protocol `_udp`, Priority `0`, Weight `0`, Port = game port, Target `rust.example.com` (Host is `rust` where offered) |
| **Porkbun** | Type `A`, Host `rust`, Answer = public IP | Type `SRV`, Host `_rust._udp.rust`, Answer `0 28015 rust.example.com`, Priority `0` |
| **AWS Route 53** | Record name `rust`, Type `A`, Value = public IP | Record name `_rust._udp.rust`, Type `SRV`, Value `0 0 28015 rust.example.com` |
| **Google Cloud DNS / Squarespace** | DNS name `rust`, Type `A`, Data = public IP | DNS name `_rust._udp.rust`, Type `SRV`, Data `0 0 28015 rust.example.com.` |
| **Zone file (BIND)** | `rust  300 IN A 203.0.113.10` | `_rust._udp.rust 300 IN SRV 0 0 28015 rust.example.com.` |

Replace `28015` with your game port and `example.com` with your domain. Some providers require a trailing
dot on the target (`rust.example.com.`); the dashboard usually adds it.

## 4. Router and firewall

- Forward **UDP game port** (default `28015`) to the server's LAN address.
- Forward **UDP query port** (HighPop default `28017`) so the server appears in the in-game browser.
- Forward **TCP Rust+ port** only if you use the Rust+ companion app.
- Do **not** forward the WebRCON port to the internet.
- HighPop can create the Windows Firewall rules: Server → Configure → Settings → *Automatically manage Windows Firewall rules when this server starts* (run HighPop as Administrator).

## 5. Tell HighPop about the domain

Set **Server → Configure → Settings → Public join domain** to the hostname (e.g. `rust.example.com`). HighPop then:

- launches Rust with `+server.favoritesEndpoint "rust.example.com"`, so players who favourite the
  server keep it after an IP or port change (Facepunch: the value is the A-record name, without
  `_rust._udp`);
- uses it in the **Custom join domain** check and shows the exact `client.connect` command to share.

## 6. Testing

From any Windows machine:

```powershell
Resolve-DnsName rust.example.com -Type A -Server 1.1.1.1
Resolve-DnsName _rust._udp.rust.example.com -Type SRV -Server 1.1.1.1
```

The A answer must be your public IP (not `104.x`/`172.64–71.x` Cloudflare addresses) and the SRV answer
must show your game port and the A-record name.

**Test the join from outside your network** (a phone hotspot or a friend). Joining your own public
domain from inside the same LAN requires NAT loopback ("hairpin NAT") on the router; many home routers
do not support it, which makes a correct setup look broken. From inside the LAN, use
`client.connect <server LAN IP>:28015` instead.

## Dynamic IP

If your public IP changes, the A record must follow it:

- **Router DDNS** — many routers can update Cloudflare or other providers directly.
- **Cloudflare API** — create an API token with only *Zone → DNS → Edit* for the zone, then run a
  scheduled task such as:

```powershell
# Updates the A record for rust.example.com to this network's public IPv4 address.
$token  = $env:CF_DNS_TOKEN          # Zone.DNS:Edit token for the zone only
$zone   = "example.com"
$record = "rust.example.com"
$h = @{ Authorization = "Bearer $token" }
$ip = ((Invoke-RestMethod https://1.1.1.1/cdn-cgi/trace) -split "`n" | Where-Object { $_ -like 'ip=*' }) -replace 'ip=',''
$zoneId = (Invoke-RestMethod "https://api.cloudflare.com/client/v4/zones?name=$zone" -Headers $h).result[0].id
$rec = (Invoke-RestMethod "https://api.cloudflare.com/client/v4/zones/$zoneId/dns_records?type=A&name=$record" -Headers $h).result[0]
if ($rec.content -ne $ip) {
  $body = @{ type = "A"; name = $record; content = $ip; ttl = 1; proxied = $false } | ConvertTo-Json
  Invoke-RestMethod -Method Put "https://api.cloudflare.com/client/v4/zones/$zoneId/dns_records/$($rec.id)" `
    -Headers $h -ContentType "application/json" -Body $body | Out-Null
}
```

Keep `proxied = $false`; turning the proxy on breaks joining.

## Troubleshooting

| HighPop check says | Fix |
|---|---|
| *does not exist in DNS* | Create the A record in the zone that owns the domain (check the Name spelling and the account). |
| *Cloudflare proxy address (orange cloud)* | Set the record to **DNS only**. |
| *private/LAN address* | Use your public IP; forward the ports on the router. |
| *Points to X, this PC's public IP is Y* | Update the A record (usually a changed dynamic IP). Expected if the server is hosted elsewhere. |
| *No SRV record* | Add `_rust._udp.<name>` → `<name>`, game port. |
| *SRV port ≠ game port* | The SRV port must be `server.port`. |
| *N SRV records* | Keep exactly one. |
| Everything ✅ but joining fails | Check UDP port forwarding and test from outside the LAN (hairpin NAT). |
