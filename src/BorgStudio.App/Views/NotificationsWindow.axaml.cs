using Avalonia.Controls;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

public partial class NotificationsWindow : Window
{
    public NotificationsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is NotificationsViewModel notifications)
            notifications.CloseRequested += (_, _) => Close();
    }
}
