using HighPop.Models;

namespace HighPop.Services;

/// <summary>
/// The single writer for durable lifecycle intent and observed phase. ServerManagerService owns
/// process execution; every process decision is recorded here before it is acted upon.
/// </summary>
public sealed class ServerLifecycleCoordinator
{
    private readonly ConfigService _config;

    public ServerLifecycleCoordinator(ConfigService config) => _config = config;

    public event Action<ServerLifecycleTransition>? Transitioned;

    public void InitializeAfterLoad(GameServer server, bool reattached)
    {
        lock (server)
        {
            ServerLifecycleRules.InitializeAfterLoad(server, reattached);
            if (reattached)
            {
                server.LifecycleGeneration++;
                server.LastLifecycleOperationId = Guid.NewGuid().ToString("N");
                server.LastLifecycleInitiator = LifecycleInitiator.Reattach;
                server.LastLifecycleReason = "Verified running process reattached";
                server.LastLifecycleTransitionUtc = DateTime.UtcNow;
            }
        }
        _config.PersistServerLifecycle(server);
    }

    public ServerLifecycleTransition RequestStart(
        GameServer server,
        LifecycleInitiator initiator,
        string reason)
        => Transition(server, ServerDesiredState.Running, ServerLifecyclePhase.Starting,
            initiator, reason, advanceGeneration: true);

    /// <summary>
    /// Persists Stopped before process shutdown begins. This is the manual-stop-wins guarantee.
    /// </summary>
    public ServerLifecycleTransition RequestStop(
        GameServer server,
        LifecycleInitiator initiator,
        string reason)
        => Transition(server, ServerDesiredState.Stopped, ServerLifecyclePhase.Stopping,
            initiator, reason, advanceGeneration: true);

    public ServerLifecycleTransition BeginRecovery(GameServer server, string reason)
        => Transition(server, null, ServerLifecyclePhase.Recovering,
            LifecycleInitiator.AlwaysOn, reason, advanceGeneration: true);

    public ServerLifecycleTransition MarkPhase(
        GameServer server,
        ServerLifecyclePhase phase,
        LifecycleInitiator initiator,
        string reason,
        string? operationId = null)
        => Transition(server, null, phase, initiator, reason,
            advanceGeneration: false, operationId);

    public bool IsCurrent(GameServer server, long generation) =>
        ServerLifecycleRules.IsCurrent(server, generation);

    private ServerLifecycleTransition Transition(
        GameServer server,
        ServerDesiredState? desiredState,
        ServerLifecyclePhase phase,
        LifecycleInitiator initiator,
        string reason,
        bool advanceGeneration,
        string? operationId = null)
    {
        ServerLifecycleTransition transition;
        lock (server)
        {
            var previous = server.LifecyclePhase;
            if (desiredState.HasValue) server.DesiredState = desiredState.Value;
            if (advanceGeneration) server.LifecycleGeneration++;

            var occurredUtc = DateTime.UtcNow;
            operationId ??= Guid.NewGuid().ToString("N");
            server.LifecyclePhase = phase;
            server.LastLifecycleOperationId = operationId;
            server.LastLifecycleReason = reason;
            server.LastLifecycleInitiator = initiator;
            server.LastLifecycleTransitionUtc = occurredUtc;

            transition = new ServerLifecycleTransition(
                server.Id,
                operationId,
                server.LifecycleGeneration,
                server.DesiredState,
                previous,
                phase,
                initiator,
                reason,
                occurredUtc);
        }

        _config.PersistServerLifecycle(server);
        var handlers = Transitioned?.GetInvocationList();
        if (handlers != null)
        {
            foreach (var handler in handlers)
            {
                try { ((Action<ServerLifecycleTransition>)handler)(transition); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Lifecycle] Transition subscriber failed: {ex}");
                }
            }
        }
        return transition;
    }
}
