# Security policy

## Supported versions

Security fixes are provided for the latest published HighPop release.

## Reporting a vulnerability

Please do not open a public issue for an unpatched vulnerability. Use GitHub's private vulnerability reporting feature on this repository and include affected versions, reproduction steps, impact, and any suggested mitigation. If private reporting is unavailable, open a minimal issue asking the maintainer to enable a private channel without publishing exploit details.

## Deployment guidance

- Keep the REST dashboard disabled unless remote access is required.
- Place remote access behind a trusted LAN, VPN, or HTTPS reverse proxy and restrict the Windows Firewall rule.
- Rotate API, RCON, Discord, SMTP, and Steam credentials after suspected exposure.
- Back up `assets/**`, but protect it as sensitive data even though supported secrets are encrypted with DPAPI.
- Review imported custom profiles, presets, server plugins, and executable files before using them.
- Run the application as a standard user except when a specific Windows configuration task requires elevation.
