using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BorgStudio.App.Views;

/// <summary>A message with one or two buttons; the dialog result is <c>true</c> for the confirm button.</summary>
public partial class MessageWindow : Window
{
    // Designer only.
    public MessageWindow() : this("Title", "Message", "OK", "Cancel")
    {
    }

    public MessageWindow(string title, string message, string confirmText, string? cancelText)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
        CancelButton.IsVisible = cancelText is not null;
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
