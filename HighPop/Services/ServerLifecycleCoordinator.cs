using HighPop.Models;
using System.Collections.Concurrent;

namespace HighPop.Services;

/// <summary>
/// The single writer for durable lifecycle intent and observed phase. ServerManagerService owns
/// process execution; every process decision is recorded here before it is acted upon.
/// </summary>
public sealed class ServerLifecycleCoordinator
{
    private readonly ConfigService _config;
    private readonly ConcurrentDictionary<string, OperationCancellation> _cancellations = new();

    private sealed record OperationCancellation(long Generation, CancellationTokenSource Source);

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

    public CancellationToken GetCancellationToken(GameServer server, long generation)
    {
        if (_cancellations.TryGetValue(server.Id, out var active)
            && active.Generation == generation)
            return active.Source.Token;
        return new CancellationToken(canceled: true);
    }

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
            if (advanceGeneration)
            {
                server.LifecycleGeneration++;
                if (_cancellations.TryRemove(server.Id, out var superseded))
                {
                    superseded.Source.Cancel();
                    superseded.Source.Dispose();
                    var prior = server.LifecycleOperationHistory.FirstOrDefault(item =>
                        item.Generation == superseded.Generation
                        && item.Status == LifecycleOperationStatus.Running);
                    if (prior != null)
                    {
                        prior.Status = LifecycleOperationStatus.Cancelled;
                        prior.Result = "Superseded by a newer lifecycle operation";
                        prior.CompletedUtc = DateTime.UtcNow;
                    }
                }
                _cancellations[server.Id] = new OperationCancellation(
                    server.LifecycleGeneration,
                    new CancellationTokenSource(ServerLifecycleRules.DefaultDeadline(server, phase)));
            }

            var occurredUtc = DateTime.UtcNow;
            operationId ??= Guid.NewGuid().ToString("N");
            server.LifecyclePhase = phase;
            server.LastLifecycleOperationId = operationId;
            server.LastLifecycleReason = reason;
            server.LastLifecycleInitiator = initiator;
            server.LastLifecycleTransitionUtc = occurredUtc;

            var status = ServerLifecycleRules.StatusForPhase(phase);
            var journal = server.LifecycleOperationHistory.FirstOrDefault(item =>
                string.Equals(item.OperationId, operationId, StringComparison.Ordinal));
            if (journal == null)
            {
                journal = new LifecycleOperationRecord
                {
                    OperationId = operationId,
                    Generation = server.LifecycleGeneration,
                    Initiator = initiator,
                    StartedUtc = occurredUtc,
                    DeadlineUtc = occurredUtc + ServerLifecycleRules.DefaultDeadline(server, phase),
                };
                server.LifecycleOperationHistory.Add(journal);
            }
            journal.DesiredState = server.DesiredState;
            journal.Phase = phase;
            journal.Status = status;
            journal.Reason = reason;
            journal.Result = status switch
            {
                LifecycleOperationStatus.Succeeded => reason,
                LifecycleOperationStatus.Failed => reason,
                _ => string.Empty,
            };
            journal.CompletedUtc = status == LifecycleOperationStatus.Running ? null : occurredUtc;
            while (server.LifecycleOperationHistory.Count > ServerLifecycleRules.MaxOperationHistory)
                server.LifecycleOperationHistory.RemoveAt(0);

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
