using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

/// <summary>Searching names in all archives of a repository; a hit's versions open from here.</summary>
public partial class ArchiveSearchWindow : Window
{
    public ArchiveSearchWindow()
    {
        InitializeComponent();
        Opened += (_, _) => SearchBox.Focus();
        // Enter opens the versions of the selected hit (the grid would move to the next row otherwise).
        HitGrid.AddHandler(KeyDownEvent, Hits_KeyDown, RoutingStrategies.Tunnel);
    }

    private ArchiveSearchViewModel? Search => DataContext as ArchiveSearchViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Search is { } search)
            search.Dialogs = new DialogService(this); // questions and the versions belong to this window
    }

    // Closed while borg reads an archive: stop it.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Search?.Abandon();
        base.OnClosing(e);
    }

    private void Hits_DoubleTapped(object? sender, TappedEventArgs e) => ShowVersions();

    private void Hits_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        ShowVersions();
        e.Handled = true;
    }

    private void ShowVersions()
    {
        if (Search?.ShowVersionsCommand.CanExecute(null) == true)
            Search.ShowVersionsCommand.Execute(null);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
