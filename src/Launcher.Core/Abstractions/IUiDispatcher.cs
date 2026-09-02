using System;
using System.Threading.Tasks;

namespace Launcher.Core.Abstractions;

/// <summary>
/// Marshals work onto the UI thread.
///
/// Exists because the previous build crashed on startup: InitializeAsync ran on a thread pool
/// thread via Task.Run and mutated an ObservableCollection already bound to the window, which
/// WPF rejects with "This type of CollectionView does not support changes to its SourceCollection
/// from a thread different from the Dispatcher thread." The exception was swallowed into a log
/// file, so the library, news and engine init all silently died.
///
/// View models take this dependency and route every collection mutation through it, so they are
/// safe no matter which thread calls them.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the caller is already on the UI thread.</summary>
    bool IsOnUiThread { get; }

    /// <summary>Run on the UI thread, returning once it has completed.</summary>
    Task InvokeAsync(Action action);

    /// <summary>Queue on the UI thread without waiting.</summary>
    void Post(Action action);
}

/// <summary>
/// Pass-through implementation for tests and headless use: everything runs inline.
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => true;

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public void Post(Action action) => action();
}
