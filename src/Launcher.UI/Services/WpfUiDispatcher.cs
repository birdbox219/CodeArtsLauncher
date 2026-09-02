using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Launcher.Core.Abstractions;

namespace Launcher.UI.Services;

/// <summary>
/// Routes view-model work onto the WPF dispatcher.
///
/// This is what stops the crash that made the library look empty: WPF refuses collection changes
/// from a non-dispatcher thread once the collection is bound, and the previous startup path did
/// exactly that from a thread-pool thread.
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher() : this(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher) { }

    public WpfUiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool IsOnUiThread => _dispatcher.CheckAccess();

    public Task InvokeAsync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    public void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }
}
