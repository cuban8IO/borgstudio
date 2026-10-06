using CommunityToolkit.Mvvm.ComponentModel;

namespace BorgStudio.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string Greeting { get; set; } = "Welcome to BorgStudio";
}
