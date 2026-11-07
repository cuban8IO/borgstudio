using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

/// <summary>The contents of an archive: folder tree, entries as a table, search, selection.</summary>
public partial class ArchiveBrowserWindow : Window
{
    /// <summary>The "Folder" column, only useful for search results.</summary>
    private const int LocationColumn = 2;

    private ArchiveBrowserViewModel? _browser;

    public ArchiveBrowserWindow()
    {
        InitializeComponent();
        // Enter opens a folder; the grid would otherwise move to the next row (so listen before it handles the key).
        EntryGrid.AddHandler(KeyDownEvent, Entries_KeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_browser is not null)
            _browser.PropertyChanged -= OnBrowserPropertyChanged;
        _browser = DataContext as ArchiveBrowserViewModel;
        if (_browser is not null)
        {
            _browser.PropertyChanged += OnBrowserPropertyChanged;
            // The restore dialog belongs to this window.
            _browser.Dialogs = new DialogService(this);
        }
        ShowLocationColumn();
    }

    private void OnBrowserPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArchiveBrowserViewModel.IsSearchResult))
            ShowLocationColumn();
    }

    private void ShowLocationColumn() => EntryGrid.Columns[LocationColumn].IsVisible = _browser?.IsSearchResult == true;

    // Double-click (or Enter) opens a folder; a file found by the search is shown in its folder.
    private void Entries_DoubleTapped(object? sender, TappedEventArgs e) => OpenSelected();

    private void Entries_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && _browser?.UpCommand.CanExecute(null) == true)
        {
            _browser.UpCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OpenSelected()
    {
        if (EntryGrid.SelectedItem is EntryViewModel entry)
            _browser?.OpenCommand.Execute(entry);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
