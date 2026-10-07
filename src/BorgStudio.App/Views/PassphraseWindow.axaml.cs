using Avalonia.Controls;
using Avalonia.Interactivity;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

/// <summary>Asks for a repository passphrase; the dialog result is the passphrase or <c>null</c>.</summary>
public partial class PassphraseWindow : Window
{
    // Designer only.
    public PassphraseWindow() : this("Repository")
    {
    }

    public PassphraseWindow(string repositoryName)
    {
        InitializeComponent();
        PromptText.Text = BorgTexts.Format(Strings.PassphrasePromptText, repositoryName);
        Opened += (_, _) => PassphraseBox.Focus();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(PassphraseBox.Text ?? "");

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
