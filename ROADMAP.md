# Roadmap

HighPop v0.1 establishes the portable Rust operations foundation. Roadmap items are prioritized by stability and self-hostability.

## Next

- Signed Windows releases and reproducible release checksums
- Rust-native timed bans, whitelist management, player notes, and bulk moderation
- Optional Carbon/uMod HighPop bridge for structured combat logs, chat, events, and richer player telemetry
- Live map adapter with explicit opt-in and documented server-plugin requirements
- SFTP/FTPS file transfer profiles for remote hosts
- Import/export bundles with secrets excluded by default

## Provider interfaces

- Pluggable VPN/proxy and geolocation lookups with caching and clear privacy controls
- Optional VAC/profile-risk sources that comply with provider terms
- Federated ban-list adapter with signatures, audit history, and per-list trust controls
- Prometheus/OpenTelemetry export and webhook event schemas

## Experience

- More languages and accessible high-contrast themes
- First-run diagnostics for NAT, firewall, SteamCMD, WebRCON, and Rust+ ports
- Preset marketplace based on signed plain JSON bundles
- Headless service mode with the desktop application acting as a client

Hosted vendor datasets and accounts will remain optional; local Rust management must continue to work without a subscription.
