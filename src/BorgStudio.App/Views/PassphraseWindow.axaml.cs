using Avalonia.Controls;
using Avalonia.Interactivity;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

/// <summary>Asks for a passphrase or another secret; the dialog result is the text or <c>null</c>.</summary>
public partial class PassphraseWindow : Window
{
    // Designer only.
    public PassphraseWindow() : this("Repository")
    {
    }

    /// <summary>Asks for the passphrase of a repository.</summary>
    public PassphraseWindow(string repositoryName)
        : this(Strings.PassphrasePromptTitle, BorgTexts.Format(Strings.PassphrasePromptText, repositoryName))
    {
    }

    public PassphraseWindow(string title, string prompt, string? error = null)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ErrorText.Text = error;
        ErrorText.IsVisible = error is not null;
        Opened += (_, _) => PassphraseBox.Focus();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(PassphraseBox.Text ?? "");

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
