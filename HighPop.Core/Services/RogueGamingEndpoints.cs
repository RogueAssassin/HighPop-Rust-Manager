namespace HighPop.Services;

/// <summary>
/// Public, read-only Rogue Gaming web endpoints HighPop talks to. Centralised so every client,
/// the trust checks for downloaded files, and the documentation agree on the same hosts.
/// </summary>
public static class RogueGamingEndpoints
{
    /// <summary>Rogue Rust Reference: static JSON API of Rust commands, ConVars and metadata.</summary>
    public const string ApiHost = "api.roguegaming.com.au";
    public const string ApiUrl = "https://" + ApiHost;

    /// <summary>RogueRust plugin catalog: documentation pages and single-file <c>.cs</c> downloads.</summary>
    public const string PluginsHost = "plugins.roguegaming.com.au";
    public const string PluginsUrl = "https://" + PluginsHost;

    public static string UserAgent => $"HighPop-Rust-Manager/{HighPop.AppInfo.Version}";

    /// <summary>True only for an absolute HTTPS URL on exactly <paramref name="host"/> (default port).</summary>
    public static bool IsHttpsOnHost(Uri? uri, string host) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);
}
