namespace HighPop.Services;

public enum UiPriority { Normal, Background }

/// <summary>The UI toolkit's thread, implemented once per toolkit (WPF, Avalonia).</summary>
public interface IUiThread
{
    /// <summary>False when there is no UI thread to run on (not started yet or shutting down).</summary>
    bool IsAvailable { get; }
    bool CheckAccess();
    void Post(Action action, UiPriority priority);
    Task InvokeAsync(Action action);
    Task<T> InvokeAsync<T>(Func<T> func);
}
