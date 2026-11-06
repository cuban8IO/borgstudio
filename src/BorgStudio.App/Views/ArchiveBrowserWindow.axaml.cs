using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

/// <summary>The contents of an archive: folder tree, entries, search, selection.</summary>
public partial class ArchiveBrowserWindow : Window
{
    public ArchiveBrowserWindow()
    {
        InitializeComponent();
    }

    private ArchiveBrowserViewModel? Browser => DataContext as ArchiveBrowserViewModel;

    // Double-click (or Enter) opens a folder; a file found by the search is shown in its folder.
    private void Entries_DoubleTapped(object? sender, TappedEventArgs e) => OpenSelected();

    private void Entries_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && Browser?.UpCommand.CanExecute(null) == true)
        {
            Browser.UpCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OpenSelected()
    {
        if (EntryList.SelectedItem is EntryViewModel entry)
            Browser?.OpenCommand.Execute(entry);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
