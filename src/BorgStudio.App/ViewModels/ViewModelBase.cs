using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BorgStudio.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    // The UI thread the view model was created on; none in tests, where everything runs directly.
    private readonly AvaloniaSynchronizationContext? _uiContext = SynchronizationContext.Current as AvaloniaSynchronizationContext;

    /// <summary>Created in the app (not in a test): there is a UI thread to come back to.</summary>
    protected bool HasUiThread => _uiContext is not null;

    /// <summary>Runs <paramref name="action"/> on the UI thread – for events raised by background work.</summary>
    protected void OnViewModelThread(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
            action();
        else
            _uiContext.Post(_ => action(), null);
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread and hands back its result – for calls from other threads.</summary>
    protected Task<T> OnViewModelThreadAsync<T>(Func<T> work)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
            return Task.FromResult(work());
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(_ =>
        {
            try
            {
                result.SetResult(work());
            }
            catch (Exception exception)
            {
                result.SetException(exception);
            }
        }, null);
        return result.Task;
    }
}
