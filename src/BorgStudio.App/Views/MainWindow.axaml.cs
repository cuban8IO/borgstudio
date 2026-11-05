using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core;

namespace BorgStudio.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => OnLockChanged();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsLocked))
            OnLockChanged();
    }

    /// <summary>Locked: close every dialog (nothing may stay visible behind the lock) and put the cursor into the PIN box.</summary>
    private void OnLockChanged()
    {
        if (_viewModel?.IsLocked != true)
            return;

        CloseDialogs(this);
        Dispatcher.UIThread.Post(() => LockScreenPinBox.Focus(), DispatcherPriority.Background);
    }

    /// <summary>Closes the dialogs of <paramref name="owner"/>, innermost first (e.g. a PIN prompt over the settings).</summary>
    private static void CloseDialogs(Window owner)
    {
        foreach (var dialog in owner.OwnedWindows.ToList())
        {
            CloseDialogs(dialog);
            dialog.Close();
        }
    }

    private async void OpenPlugins_Click(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel { IsLocked: false } viewModel)
            return;

        var window = new PluginsWindow { DataContext = new PluginsViewModel(viewModel.Plugins, AppPaths.PluginsDirectory) };
        await window.ShowDialog(this);
    }

    private async void OpenKeys_Click(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel { IsLocked: false } viewModel)
            return;

        var window = new KeysWindow();
        window.DataContext = new KeysViewModel(viewModel, new DialogService(window));
        await window.ShowDialog(this);
    }

    private async void OpenSettings_Click(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel { IsLocked: false } viewModel)
            return;

        var window = new SettingsWindow();
        window.DataContext = new SettingsViewModel(viewModel, new DialogService(window));
        await window.ShowDialog(this);
    }
}
