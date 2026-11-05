using Avalonia.Controls;
using Avalonia.Interactivity;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Security;

namespace BorgStudio.App.Views;

/// <summary>Asks for the vault PIN, or for a new one (twice); the dialog result is the PIN or <c>null</c>.</summary>
public partial class PinWindow : Window
{
    public enum Mode
    {
        Unlock,
        Create,
    }

    private readonly Mode _mode;

    // Designer only.
    public PinWindow() : this(Mode.Create)
    {
    }

    public PinWindow(Mode mode, string? error = null)
    {
        _mode = mode;
        InitializeComponent();

        Title = mode == Mode.Unlock ? Strings.PinUnlockTitle : Strings.PinCreateTitle;
        IntroText.Text = mode == Mode.Unlock ? Strings.PinUnlockText : Strings.PinCreateText;
        PinLabel.Text = mode == Mode.Unlock ? Strings.PinLabel : Strings.PinNewLabel;
        ConfirmPanel.IsVisible = mode == Mode.Create;
        HintText.Text = BorgTexts.Format(Strings.PinHint, Vault.MinimumPinLength);
        ShowError(error);
        Opened += (_, _) => PinBox.Focus();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var pin = PinBox.Text ?? "";
        if (_mode == Mode.Create)
        {
            if (!Vault.IsValidPin(pin))
            {
                ShowError(BorgTexts.Format(Strings.PinTooShort, Vault.MinimumPinLength));
                return;
            }

            if (pin != ConfirmBox.Text)
            {
                ShowError(Strings.PinMismatch);
                return;
            }
        }
        else if (pin.Length == 0)
        {
            return;
        }

        Close(pin);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void ShowError(string? error)
    {
        ErrorText.Text = error;
        ErrorText.IsVisible = error is not null;
    }
}
