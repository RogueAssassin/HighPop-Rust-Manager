using HighPop.Models;

namespace HighPop.Services;

public readonly record struct NetworkRuleUpdateResult(bool Success, string Message);

/// <summary>
/// Manages Windows Firewall rules via the native COM Firewall API (HNetCfg.FwPolicy2) instead
/// of shelling out to netsh.exe. Spawning netsh.exe to silently add firewall rules is a classic
/// "Impair Defenses" pattern (MITRE ATT&CK T1562.004) that Defender's behavioral heuristics flag,
/// even though the actual purpose here — opening a game server's own ports — is legitimate.
/// </summary>
public static class FirewallService
{
    private const string Prefix = "HighPop - ";
    private const int NET_FW_IP_PROTOCOL_TCP = 6;
    private const int NET_FW_IP_PROTOCOL_UDP = 17;
    private const int NET_FW_RULE_DIR_IN     = 1;
    private const int NET_FW_ACTION_ALLOW    = 1;
    private const int NET_FW_PROFILE2_ALL    = 0x7FFFFFFF;

    public static NetworkRuleUpdateResult AddRules(GameServer server)
    {
        var name = RuleName(server.DisplayName);
        RemoveRules(server); // avoid duplicates

        var desired = new List<(string Name, int Port, int Protocol)>
        {
            ($"{name} (Game UDP)", server.ServerPort, NET_FW_IP_PROTOCOL_UDP),
            ($"{name} (Game TCP)", server.ServerPort, NET_FW_IP_PROTOCOL_TCP),
        };

        if (server.QueryPort > 0 && server.QueryPort != server.ServerPort)
            desired.Add(($"{name} (Query)", server.QueryPort, NET_FW_IP_PROTOCOL_UDP));

        if (server.RconPort > 0)
            desired.Add(($"{name} (RCON)", server.RconPort, NET_FW_IP_PROTOCOL_TCP));

        if (server.GameSpecificSettings.TryGetValue("appPort", out var appPortText)
            && int.TryParse(appPortText, out var appPort) && appPort > 0)
            desired.Add(($"{name} (Rust+)", appPort, NET_FW_IP_PROTOCOL_TCP));

        foreach (var rule in desired)
        {
            if (AddRule(rule.Name, rule.Port, rule.Protocol, out var error)) continue;
            foreach (var added in desired) RemoveRule(added.Name);
            return new NetworkRuleUpdateResult(false,
                $"Firewall changes were rolled back: {error}");
        }

        return new NetworkRuleUpdateResult(true, $"Configured {desired.Count} firewall rules.");
    }

    public static void RemoveRules(GameServer server)
    {
        var name = RuleName(server.DisplayName);
        RemoveRule(name);
        RemoveRule($"{name} (Game UDP)");
        RemoveRule($"{name} (Game TCP)");
        RemoveRule($"{name} (Query)");
        RemoveRule($"{name} (RCON)");
        RemoveRule($"{name} (Rust+)");
    }

    private static string RuleName(string displayName)
        => Prefix + string.Concat(displayName.Split('<', '>', '"', '&', '|'));

    private static dynamic? CreatePolicy()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
        return type == null ? null : Activator.CreateInstance(type);
    }

    private static bool AddRule(string name, int port, int protocol, out string error)
    {
        try
        {
            dynamic? policy = CreatePolicy();
            if (policy == null)
            {
                error = "Windows Firewall API is unavailable.";
                return false;
            }

            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
            if (ruleType == null)
            {
                error = "Windows Firewall rule API is unavailable.";
                return false;
            }
            dynamic rule = Activator.CreateInstance(ruleType)!;

            rule.Name       = name;
            rule.Protocol   = protocol;
            rule.LocalPorts = port.ToString();
            rule.Direction  = NET_FW_RULE_DIR_IN;
            rule.Action     = NET_FW_ACTION_ALLOW;
            rule.Enabled    = true;
            rule.Profiles   = NET_FW_PROFILE2_ALL;

            policy.Rules.Add(rule);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void RemoveRule(string name)
    {
        try { CreatePolicy()?.Rules.Remove(name); }
        catch { }
    }
}
