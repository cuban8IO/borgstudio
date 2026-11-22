using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BorgStudio.App.Views;

/// <summary>Asks for a line of text (a name, a comment); the dialog result is the text or <c>null</c>.</summary>
public partial class TextInputWindow : Window
{
    // Designer only.
    public TextInputWindow() : this("Title", "Prompt", "")
    {
    }

    public TextInputWindow(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Opened += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(InputBox.Text ?? "");

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
