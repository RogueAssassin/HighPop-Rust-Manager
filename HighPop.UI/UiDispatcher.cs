namespace HighPop.Services;

/// <summary>
/// One place for every hop onto the UI thread, whichever toolkit hosts it.
///
/// Background services (process Exited handlers, log tailers, RCON, Discord, the Web API,
/// timers) raise events on thread-pool threads. Marshalling those with a blocking invoke means
/// the background thread waits for the UI thread; if the UI thread is itself waiting on that
/// background work (directly, or through a lock or a sync-over-async call) the application
/// deadlocks and the OS reports "Not Responding".
///
/// Rules used across HighPop:
/// <list type="bullet">
/// <item><see cref="Post"/>: fire-and-forget UI updates from events. Never blocks the caller.</item>
/// <item><see cref="RunAsync(Action)"/> / <see cref="RunAsync{T}(Func{T})"/>: awaitable UI work from async code.</item>
/// <item><see cref="Read{T}"/>: a synchronous read of UI-owned state for a background caller,
///   bounded by a timeout so a busy or blocked UI thread can never hang the caller forever.</item>
/// </list>
/// Each helper runs inline when the caller is already on the UI thread.
/// </summary>
public static class UiDispatcher
{
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Set once by the app at startup. Null in tests and in the headless host.</summary>
    public static IUiThread? Thread { get; set; }

    private static IUiThread? UiThread => Thread is { IsAvailable: true } thread ? thread : null;

    /// <summary>True when the caller is on the UI thread, or no UI thread exists (tests).</summary>
    public static bool CheckAccess() => UiThread?.CheckAccess() ?? true;

    /// <summary>Queues <paramref name="action"/> on the UI thread without waiting.</summary>
    public static void Post(Action action, UiPriority priority = UiPriority.Normal)
    {
        var ui = UiThread;
        if (ui == null) return;
        if (ui.CheckAccess())
        {
            SafeRun(action);
            return;
        }
        try { ui.Post(() => SafeRun(action), priority); }
        catch (InvalidOperationException) { /* UI thread is shutting down */ }
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread and completes when it has run.</summary>
    public static Task RunAsync(Action action)
    {
        var ui = UiThread;
        if (ui == null || ui.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return ui.InvokeAsync(action);
    }

    /// <summary>Evaluates <paramref name="func"/> on the UI thread and returns its result.</summary>
    public static Task<T> RunAsync<T>(Func<T> func)
    {
        var ui = UiThread;
        if (ui == null || ui.CheckAccess())
            return Task.FromResult(func());
        return ui.InvokeAsync(func);
    }

    /// <summary>Runs an async UI-bound operation (e.g. a RelayCommand) on the UI thread.</summary>
    public static Task RunTaskAsync(Func<Task> func)
    {
        var ui = UiThread;
        if (ui == null || ui.CheckAccess())
            return func();
        return ui.InvokeAsync(func).Unwrap();
    }

    /// <summary>
    /// Synchronously reads UI-owned state for a background caller. Returns
    /// <paramref name="fallback"/> if the UI thread does not service the request within
    /// <paramref name="timeout"/> (default 5 s) instead of hanging the caller.
    /// </summary>
    public static T Read<T>(Func<T> func, T fallback, TimeSpan? timeout = null)
    {
        var ui = UiThread;
        if (ui == null || ui.CheckAccess())
            return func();
        try
        {
            var operation = ui.InvokeAsync(func);
            return operation.Wait(timeout ?? DefaultReadTimeout) ? operation.Result : fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException
                                       or OperationCanceledException or AggregateException)
        {
            return fallback;
        }
    }

    private static void SafeRun(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UiDispatcher] UI callback failed: {ex}");
        }
    }
}
