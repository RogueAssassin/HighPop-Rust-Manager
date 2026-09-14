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
}
