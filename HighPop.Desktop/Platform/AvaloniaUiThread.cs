using Avalonia.Threading;
using HighPop.Services;

namespace HighPop.Desktop.Platform;

/// <summary>The Avalonia UI thread, plugged into <see cref="UiDispatcher"/>.</summary>
internal sealed class AvaloniaUiThread : IUiThread
{
    public bool IsAvailable => true;

    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action, UiPriority priority) =>
        Dispatcher.UIThread.Post(action,
            priority == UiPriority.Background ? DispatcherPriority.Background : DispatcherPriority.Normal);

    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    public Task<T> InvokeAsync<T>(Func<T> func) => Dispatcher.UIThread.InvokeAsync(func).GetTask();
}
