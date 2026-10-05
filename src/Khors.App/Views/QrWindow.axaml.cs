using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Khors.App.Views;

public partial class QrWindow : Window
{
    public QrWindow() => InitializeComponent();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
