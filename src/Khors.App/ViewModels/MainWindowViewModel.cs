using CommunityToolkit.Mvvm.ComponentModel;
using Khors.App.Resources;

namespace Khors.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _status = Strings.StatusDisconnected;
}
