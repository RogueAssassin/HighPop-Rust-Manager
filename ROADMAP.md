# Roadmap

HighPop v0.3 expands the Rust-native administration stage into a clearer, more resilient operations workspace. Roadmap items are prioritized by stability and self-hostability.

## Completed in v0.3

- Auto-reconnecting WebRCON and improved player session collection
- Dedicated Rust browser tag, profile, `serverauto.cfg`, and log-path controls
- Carbon/Oxide detection with plugin and plugin-config inventories
- Serialized scheduled operations with visible outcomes
- Detailed resource/network/player charts and expanded operational audit entries
- Integrated Discord status boards and private administrator controls

## Next

- Signed Windows releases and reproducible release checksums
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
