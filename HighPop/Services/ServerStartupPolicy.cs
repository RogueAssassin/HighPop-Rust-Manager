using HighPop.Models;

namespace HighPop.Services;

public static class ServerStartupPolicy
{
    /// <summary>
    /// Opening HighPop may start a stopped server only when AutoStart is enabled.
    /// KeepOnline is a recovery policy that becomes active for a process HighPop
    /// started deliberately or successfully reattached; it is not a startup request.
    /// </summary>
    public static bool ShouldStartOnManagerLaunch(GameServer server, bool reattached) =>
        server.AutoStart && !reattached;
}
