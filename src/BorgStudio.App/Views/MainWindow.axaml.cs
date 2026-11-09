using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using BorgStudio.App.Resources;
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

    /// <summary>Set once the user agreed to stop a running backup; the window then closes when borg has stopped.</summary>
    private bool _closeAfterBackup;

    /// <summary>Set once the mounts are gone; the window then closes for good.</summary>
    private bool _unmounted;

    /// <summary>Start the app again once this window has closed.</summary>
    private bool _restart;

    /// <summary>Started instead of the app itself (an update's installer).</summary>
    private ProcessStartInfo? _restartWith;

    /// <summary>Closes the app the usual way (a running backup asks first) and starts it again, or <paramref name="instead"/>.</summary>
    public void Restart(ProcessStartInfo? instead = null)
    {
        _restart = true;
        _restartWith = instead;
        CloseDialogs(this);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_restart)
            StartAgain(_restartWith);
    }

    private static void StartAgain(ProcessStartInfo? instead)
    {
        if (instead is not null)
        {
            Process.Start(instead)?.Dispose();
            return;
        }
        if (Environment.ProcessPath is not { } path)
            return;
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        // Started as "dotnet BorgStudio.dll" the dll is the first argument and must stay.
        var arguments = Environment.GetCommandLineArgs();
        var isDotnet = Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        foreach (var argument in isDotnet ? arguments : arguments.Skip(1))
            start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_viewModel is { IsBackupRunning: true } viewModel && !_closeAfterBackup)
        {
            e.Cancel = true;
            _ = StopBackupAndCloseAsync(viewModel);
        }
        else if (_viewModel is { HasMounts: true } mounted && !_unmounted)
        {
            // Mounts must not outlive the app (borg would keep the repository locked).
            e.Cancel = true;
            _ = UnmountAndCloseAsync(mounted);
        }

        base.OnClosing(e);
    }

    private async Task UnmountAndCloseAsync(MainViewModel viewModel)
    {
        IsEnabled = false;
        await viewModel.UnmountAllAsync();
        _unmounted = true;
        Close();
    }

    private async Task StopBackupAndCloseAsync(MainViewModel viewModel)
    {
        if (!await new DialogService(this).ConfirmAsync(Strings.BackupRunningCloseTitle, Strings.BackupRunningCloseMessage,
                Strings.BackupRunningCloseConfirm))
        {
            _restart = false;
            return;
        }

        _closeAfterBackup = true;
        viewModel.CancelBackupCommand.Execute(null);
        await viewModel.BackupFinished;
        Close();
    }

    /// <summary>A double-click on an archive opens it.</summary>
    private void Archives_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is MainViewModel { OpenArchiveCommand: var open } && open.CanExecute(null))
            open.Execute(null);
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

    private async void OpenExport_Click(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel { IsLocked: false } viewModel)
            return;

        var window = new ExportWindow();
        window.DataContext = new ExportViewModel(viewModel, new DialogService(window));
        await window.ShowDialog(this);
    }

    private async void OpenImport_Click(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel { IsLocked: false } viewModel)
            return;

        // File and password first; the preview window only opens for a readable export.
        if (await ImportViewModel.OpenAsync(viewModel, new DialogService(this)) is not { } import)
            return;
        await new ImportWindow { DataContext = import }.ShowDialog(this);
    }

    private async void OpenUpdate_Click(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel { IsLocked: false } viewModel)
            await viewModel.ShowUpdateCommand.ExecuteAsync(null);
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
