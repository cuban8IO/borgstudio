using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BorgStudio.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    // The UI thread the view model was created on; none in tests, where everything runs directly.
    private readonly AvaloniaSynchronizationContext? _uiContext = SynchronizationContext.Current as AvaloniaSynchronizationContext;

    /// <summary>Runs <paramref name="action"/> on the UI thread – for events raised by background work.</summary>
    protected void OnViewModelThread(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
            action();
        else
            _uiContext.Post(_ => action(), null);
    }
}
