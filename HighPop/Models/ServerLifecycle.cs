namespace HighPop.Models;

/// <summary>What HighPop is durably expected to do with a server.</summary>
public enum ServerDesiredState
{
    Unspecified,
    Stopped,
    Running,
    Maintenance,
}

/// <summary>What HighPop currently observes or is doing.</summary>
public enum ServerLifecyclePhase
{
    Unknown,
    StoppedByOperator,
    Starting,
    ProcessRunning,
    RustReady,
    RconReady,
    Online,
    Stopping,
    Recovering,
    Maintenance,
    Degraded,
    Faulted,
}

public enum LifecycleInitiator
{
    Unknown,
    Operator,
    AutoStart,
    AlwaysOn,
    Scheduler,
    HealthCheck,
    WakeOnDemand,
    LogRule,
    Discord,
    WebApi,
    UpdateWorkflow,
    Reattach,
    ManagerExit,
}

public enum LifecycleOperationStatus
{
    Running,
    Succeeded,
    Cancelled,
    Failed,
}

/// <summary>A compact durable audit entry. Profiles retain only the newest entries.</summary>
public sealed class LifecycleOperationRecord
{
    public string OperationId { get; set; } = string.Empty;
    public long Generation { get; set; }
    public LifecycleInitiator Initiator { get; set; }
    public ServerDesiredState DesiredState { get; set; }
    public ServerLifecyclePhase Phase { get; set; }
    public LifecycleOperationStatus Status { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime DeadlineUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

public sealed record ServerLifecycleTransition(
    string ServerId,
    string OperationId,
    long Generation,
    ServerDesiredState DesiredState,
    ServerLifecyclePhase PreviousPhase,
    ServerLifecyclePhase Phase,
    LifecycleInitiator Initiator,
    string Reason,
    DateTime OccurredUtc);

/// <summary>Pure lifecycle rules shared by runtime code and smoke tests.</summary>
public static class ServerLifecycleRules
{
    public const int MaxOperationHistory = 32;

    public static void InitializeAfterLoad(GameServer server, bool reattached)
    {
        if (reattached)
        {
            server.DesiredState = ServerDesiredState.Running;
            server.LifecyclePhase = ServerLifecyclePhase.Online;
            return;
        }

        // Legacy profiles have no durable intent and must remain stopped unless Auto-start is
        // independently enabled. A v0.8.1 profile that already persisted Running retains that
        // explicit intent even if its old PID can no longer be reattached.
        if (server.DesiredState == ServerDesiredState.Unspecified)
        {
            server.DesiredState = ServerDesiredState.Stopped;
            server.LifecyclePhase = ServerLifecyclePhase.StoppedByOperator;
            return;
        }

        server.LifecyclePhase = server.DesiredState switch
        {
            ServerDesiredState.Running => ServerLifecyclePhase.Recovering,
            ServerDesiredState.Maintenance => ServerLifecyclePhase.Maintenance,
            _ => ServerLifecyclePhase.StoppedByOperator,
        };
    }

    public static bool CanRecover(GameServer server) =>
        server.DesiredState == ServerDesiredState.Running
        && (server.KeepOnline || server.AutoRestart);

    public static bool IsCurrent(GameServer server, long generation) =>
        server.LifecycleGeneration == generation;

    public static LifecycleOperationStatus StatusForPhase(ServerLifecyclePhase phase) => phase switch
    {
        ServerLifecyclePhase.StoppedByOperator or ServerLifecyclePhase.Online
            or ServerLifecyclePhase.RustReady or ServerLifecyclePhase.RconReady
            or ServerLifecyclePhase.Maintenance
            => LifecycleOperationStatus.Succeeded,
        ServerLifecyclePhase.Faulted or ServerLifecyclePhase.Degraded
            => LifecycleOperationStatus.Failed,
        _ => LifecycleOperationStatus.Running,
    };

    public static TimeSpan DefaultDeadline(GameServer server, ServerLifecyclePhase phase) => phase switch
    {
        ServerLifecyclePhase.Stopping => TimeSpan.FromSeconds(
            Math.Clamp(server.GracefulStopTimeoutSeconds, 15, 600) + 30),
        ServerLifecyclePhase.Starting or ServerLifecyclePhase.Recovering => TimeSpan.FromMinutes(
            Math.Clamp(server.RconAutoConnectTimeoutMinutes, 1, 60) + 5),
        _ => TimeSpan.FromMinutes(15),
    };
}

/// <summary>Pure bounded backoff used by automatic WebRCON reconnect cycles.</summary>
public static class RconReconnectPolicy
{
    public static TimeSpan GetDelay(int failedAttempt, double jitter)
    {
        failedAttempt = Math.Clamp(failedAttempt, 1, 20);
        jitter = Math.Clamp(jitter, 0.8, 1.2);
        var exponentialSeconds = Math.Min(45, 5 * Math.Pow(1.7, failedAttempt - 1));
        return TimeSpan.FromSeconds(Math.Max(1, exponentialSeconds * jitter));
    }
}
