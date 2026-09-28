using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using HighPop.Models;

namespace HighPop.Services;

public readonly record struct NetworkRuleUpdateResult(
    bool Success, string Message, bool RequiresElevation = false);

/// <summary>
/// Manages inbound rules through Windows Firewall with Advanced Security (INetFwPolicy2).
/// That API is available from Windows Vista/Server 2008 onward; HPRM's .NET 10 runtime has
/// a narrower supported-OS range. No localized command output or PowerShell module is required.
/// </summary>
public static class FirewallService
{
    private const string Prefix = "HighPop - ";
    private const string GroupName = "HighPop Rust Manager";
    private const int Tcp = 6;
    private const int Udp = 17;
    private const int Inbound = 1;
    private const int Allow = 1;
    private const int AllProfiles = 0x7FFFFFFF;

    private readonly record struct RuleSpec(string Name, int Port, int Protocol);

    public static NetworkRuleUpdateResult AddRules(GameServer server)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, "Windows Firewall management is only available on Windows.");
        if (!IsAdministrator())
            return new(false,
                "Firewall rules were not changed because HighPop is not running as Administrator. " +
                "Restart HighPop as Administrator, or turn off automatic firewall management for this server.",
                RequiresElevation: true);

        var desired = BuildRules(server);
        var invalid = desired.FirstOrDefault(rule => rule.Port is < 1 or > 65535);
        if (!string.IsNullOrEmpty(invalid.Name))
            return new(false, $"Firewall rule '{invalid.Name}' has invalid port {invalid.Port}.");

        dynamic? policy = null;
        try
        {
            policy = CreateComObject("HNetCfg.FwPolicy2");
            if (policy == null)
                return new(false, "Windows Firewall with Advanced Security is unavailable on this system.");

            foreach (var rule in AllKnownRules(server))
                TryRemoveRule(policy, rule.Name);

            var added = new List<RuleSpec>();
            foreach (var rule in desired)
            {
                try
                {
                    AddRule(policy, rule);
                    added.Add(rule);
                }
                catch (Exception ex)
                {
                    foreach (var rollback in added)
                        TryRemoveRule(policy, rollback.Name);
                    return Failure("Firewall changes were rolled back", ex);
                }
            }

            var policyNote = GetPolicyNote(policy);
            return new(true,
                $"Configured {desired.Count} firewall rules for all network profiles.{policyNote}");
        }
        catch (Exception ex)
        {
            return Failure("Firewall rules could not be configured", ex);
        }
        finally
        {
            ReleaseComObject(policy);
        }
    }

    public static void RemoveRules(GameServer server)
    {
        if (!OperatingSystem.IsWindows() || !IsAdministrator()) return;

        dynamic? policy = null;
        try
        {
            policy = CreateComObject("HNetCfg.FwPolicy2");
            if (policy == null) return;
            foreach (var rule in AllKnownRules(server))
                TryRemoveRule(policy, rule.Name);
        }
        catch { }
        finally { ReleaseComObject(policy); }
    }

    private static List<RuleSpec> BuildRules(GameServer server)
    {
        var name = RuleName(server.DisplayName);
        var desired = new List<RuleSpec>
        {
            new($"{name} (Game UDP)", server.ServerPort, Udp),
            new($"{name} (Game TCP)", server.ServerPort, Tcp),
        };

        if (server.QueryPort > 0 && server.QueryPort != server.ServerPort)
            desired.Add(new($"{name} (Query)", server.QueryPort, Udp));
        if (server.RconPort > 0)
            desired.Add(new($"{name} (RCON)", server.RconPort, Tcp));
        if (server.GameSpecificSettings.TryGetValue("appPort", out var appPortText)
            && int.TryParse(appPortText, out var appPort) && appPort > 0)
            desired.Add(new($"{name} (Rust+)", appPort, Tcp));

        return desired;
    }

    private static IEnumerable<RuleSpec> AllKnownRules(GameServer server)
    {
        var name = RuleName(server.DisplayName);
        yield return new(name, 0, 0); // legacy pre-v0.8 rule
        yield return new($"{name} (Game UDP)", 0, 0);
        yield return new($"{name} (Game TCP)", 0, 0);
        yield return new($"{name} (Query)", 0, 0);
        yield return new($"{name} (RCON)", 0, 0);
        yield return new($"{name} (Rust+)", 0, 0);
    }

    private static string RuleName(string displayName)
    {
        var safeName = string.Concat((displayName ?? string.Empty)
            .Where(character => character is not '<' and not '>' and not '"' and not '&' and not '|'))
            .Trim();
        if (safeName.Length == 0) safeName = "Rust server";
        if (safeName.Length > 180) safeName = safeName[..180];
        return Prefix + safeName;
    }

    private static object? CreateComObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: false);
        return type == null ? null : Activator.CreateInstance(type);
    }

    private static void AddRule(dynamic policy, RuleSpec spec)
    {
        dynamic? rule = null;
        try
        {
            rule = CreateComObject("HNetCfg.FWRule")
                ?? throw new InvalidOperationException("Windows Firewall rule API is unavailable.");
            rule.Name = spec.Name;
            rule.Description = "Inbound Rust server port managed by HighPop Rust Manager.";
            rule.Grouping = GroupName;
            rule.Protocol = spec.Protocol;
            rule.LocalPorts = spec.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rule.Direction = Inbound;
            rule.Action = Allow;
            rule.Enabled = true;
            rule.Profiles = AllProfiles;
            policy.Rules.Add(rule);
        }
        finally
        {
            ReleaseComObject(rule);
        }
    }

    private static void TryRemoveRule(dynamic policy, string name)
    {
        try { policy.Rules.Remove(name); }
        catch { }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string GetPolicyNote(dynamic policy)
    {
        try
        {
            var modifyState = (int)policy.LocalPolicyModifyState;
            return modifyState switch
            {
                1 => " Group Policy can override local firewall rules on this machine.",
                2 => " Inbound connections are currently blocked by local firewall policy.",
                _ => string.Empty,
            };
        }
        catch { return string.Empty; }
    }

    private static NetworkRuleUpdateResult Failure(string prefix, Exception exception)
    {
        var root = exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException
            : exception;
        var hresult = root.HResult;
        var message = hresult switch
        {
            unchecked((int)0x80070005) =>
                "access was denied. Restart HighPop as Administrator and try again",
            unchecked((int)0x800706D9) =>
                "the Windows Defender Firewall service is stopped or unavailable. Start the service and try again",
            unchecked((int)0x800704EC) =>
                "local firewall changes are blocked by Group Policy. Ask the Windows administrator to add the ports",
            unchecked((int)0x80040154) =>
                "the Windows Firewall API is not registered on this Windows installation",
            _ => string.IsNullOrWhiteSpace(root.Message)
                ? $"Windows returned HRESULT 0x{hresult:X8}"
                : root.Message,
        };
        return new(false, $"{prefix}: {message}.", hresult == unchecked((int)0x80070005));
    }

    private static void ReleaseComObject(object? value)
    {
        if (value == null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); }
        catch { }
    }
}
